using System.Diagnostics;
using System.Text;
using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Connections;
using Bmt.Core.Metrics;
using Bmt.Core.Models;
using Bmt.LoadGen.Output;
using Bmt.LoadGen.Scenarios;
using Bmt.Preflight;
using MongoDB.Driver;

namespace Bmt.LoadGen;

/// <summary>
/// Orchestrates one <c>test</c> invocation end-to-end (test_instruction.md §6): warm the data cache
/// (untimed pre-read, no retained connections) → run the mandatory preflight gate (abort on any FAIL
/// unless <c>--no-preflight</c>) → execute N iterations of the selected scenario(s) under the no-reuse
/// per-Task connection model → write per-iteration JSON + CSV artifacts → write a cross-iteration
/// aggregate.json. Targets run one at a time; this drives exactly one target per invocation.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly BmtConfig _config;
    private readonly RunOptions _options;
    private readonly string _connectionString;

    public RunOrchestrator(BmtConfig config, RunOptions options)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionString = TargetConnection.ResolveConnectionString(options.Target);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var target = _options.Target;
        var cliName = TargetConnection.CliName(target);
        var workloadToken = _config.Workload.Token();
        var iterations = _config.Scenario.Iterations;

        ConsoleLog.Info($"=== LoadGen run: target={cliName} scenario={_options.Scenario} " +
                        $"workload={workloadToken} iterations={iterations} ===");
        ConsoleLog.Info($"Connection: {ConnectionStringMasker.Mask(_connectionString)}");

        // ---- Preflight gate (§6.3) — runs once before all iterations ----
        var gate = new PreflightGateInfo { Ran = false, Outcome = "skipped" };
        if (_options.RunPreflight)
        {
            ConsoleLog.Info("Running preflight gate...");
            var preflight = new PreflightRunner(_config, target, warmup: false, verifyDistinct: false, hostCount: _options.HostCount);
            var report = await preflight.RunAsync(ct).ConfigureAwait(false);
            gate = new PreflightGateInfo
            {
                Ran = true,
                Passed = report.CanProceed,
                Outcome = report.Outcome.ToString(),
                InputIndexUnique = report.IndexPolicy.InputIndexUnique,
                IndexUniquenessDiverges = report.IndexPolicy.UniquenessDivergesFromCanonical,
                DistinctReqIdGuarantee = report.IndexPolicy.DistinctReqIdGuarantee,
            };

            if (!report.CanProceed)
            {
                ConsoleLog.Error("Preflight FAILED — aborting run (results would be invalid). " +
                                 "Re-run after seeding/fixing, or pass --no-preflight to bypass (NOT recommended).");
                return 3;
            }

            ConsoleLog.Info($"Preflight {report.Outcome}: may proceed.");
        }
        else
        {
            ConsoleLog.Warn("Preflight SKIPPED (--no-preflight). Dataset/index preconditions are NOT verified.");
        }

        // ---- Determine ReqId space from the live dataset ----
        var datasetCount = await CountInputAsync(ct).ConfigureAwait(false);
        if (datasetCount <= 0)
        {
            ConsoleLog.Error("calc_input is empty — nothing to load against. Seed the dataset first.");
            return 3;
        }

        ConsoleLog.Info($"Dataset: calc_input has {datasetCount} documents (ReqId space \"1\"..\"{datasetCount}\").");

        // ---- Warm the data cache once (untimed; no retained connections) ----
        await WarmCacheAsync(datasetCount, ct).ConfigureAwait(false);

        // ---- Determine effective per-iteration duration ----
        // Priority: CLI --duration-sec > config Scenario.IterationDurationSeconds > per-scenario defaults.
        var effectiveDurationSec = _options.DurationSecondsOverride > 0
            ? _options.DurationSecondsOverride
            : _config.Scenario.IterationDurationSeconds > 0
                ? _config.Scenario.IterationDurationSeconds
                : 0; // 0 = each scenario uses its own DurationSeconds

        // ---- Resolve active scenarios: CLI --scenario selects candidates, config Enabled gates them.
        // Keeping Steady and Burst separate (enable only one) avoids stacking their arrival rates. ----
        var (runSteady, runBurst) = ResolveActiveScenarios();
        if (!runSteady && !runBurst)
        {
            ConsoleLog.Error(
                $"No scenario active: --scenario={_options.Scenario} with " +
                $"Steady.Enabled={_config.Scenario.Steady.Enabled}, Burst.Enabled={_config.Scenario.Burst.Enabled}. " +
                "Enable at least one scenario in config (or change --scenario).");
            return 3;
        }

        ConsoleLog.Info($"Active scenarios: Steady={runSteady}, Burst={runBurst}.");

        if (_options.HostCount > 1 || _options.HostId > 1 || !string.IsNullOrEmpty(_options.RunTag))
        {
            ConsoleLog.Info(
                $"Multi-host campaign: host {_options.HostId}/{_options.HostCount}" +
                (string.IsNullOrEmpty(_options.RunTag) ? "" : $" tag='{_options.RunTag}'") +
                $". Per-host RNG seed offset = {_options.HostId}.");
        }

        // ---- Optional coordinated start: all hosts begin the timed phase at the SAME wall-clock
        // instant so their bursts overlap and combined conn/s + concurrency provably sum (§6.2). ----
        await WaitForCoordinatedStartAsync(ct).ConfigureAwait(false);

        // ---- Campaign folder (shared by all iterations + aggregate) ----
        // Compact, human-scannable name: <db>-<loop>-<workload>-<MMdd>-<stamp>[-hN].
        //   db       mongo | mongovm | docdb | cosmos
        //   loop     open (open-loop, arrival-rate driven) | closed (closed-loop, gated task ceiling)
        //   workload full (4-op cycle) | query (single-op find) | insert (single-op insert)
        //   MMdd     month-day of the run
        //   stamp    ≤3-char base-36 of the start instant — unique per run
        //   -hN      host id, only when this is a multi-host campaign (keeps each host's folder distinct)
        // The date + stamp derive from the coordinated start instant (identical across all hosts of a
        // campaign) so every host's folder shares one base and differs only by the -hN token. The
        // grouping RunTag still lives in each result JSON (used by `merge`), so shortening the folder
        // name here does not affect cross-host aggregation.
        var stampSeed = _options.StartAtUtc ?? DateTime.UtcNow;
        var mmdd = stampSeed.ToString("MMdd");
        var stamp = Base36Suffix(((DateTimeOffset)stampSeed).ToUnixTimeSeconds(), 3);
        var hostSuffix = _options.HostCount > 1 ? $"-h{_options.HostId}" : string.Empty;
        var campaignId = $"{DbLabel(cliName)}-{LoopLabel()}-{WorkloadLabel()}-{mmdd}-{stamp}{hostSuffix}";
        var campaignDir = Path.Combine(_options.ResultsDirectory, campaignId);
        Directory.CreateDirectory(campaignDir);
        ConsoleLog.Info($"Campaign folder: {campaignDir}");

        // ---- Run iterations ----
        var iterResults = new List<RunResult>(iterations);
        var artifactRelPaths = new List<string>(iterations);

        for (var iter = 1; iter <= iterations; iter++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            ConsoleLog.Info($"");
            ConsoleLog.Info($">>> Iteration {iter}/{iterations} <<<");

            var (result, relPath) = await RunIterationAsync(
                iter, iterations, campaignId, campaignDir,
                datasetCount, effectiveDurationSec, gate, cliName, ct).ConfigureAwait(false);

            iterResults.Add(result);
            artifactRelPaths.Add(relPath);
        }

        // ---- Write aggregate ----
        if (iterResults.Count > 0)
        {
            var agg = AggregateResult.Build(iterResults, artifactRelPaths);
            var aggPath = Path.Combine(campaignDir, "aggregate.json");
            await File.WriteAllTextAsync(aggPath, agg.ToJson(), ct).ConfigureAwait(false);
            ConsoleLog.Info($"Wrote aggregate: {aggPath}");
            PrintAggregateSummary(agg);
        }

        return 0;
    }

    private async Task<(RunResult result, string relPath)> RunIterationAsync(
        int iterNumber,
        int totalIters,
        string campaignId,
        string campaignDir,
        long datasetCount,
        int effectiveDurationSec,
        PreflightGateInfo gate,
        string cliName,
        CancellationToken ct)
    {
        // ---- Wire fresh metrics + per-Task no-reuse connection factory (new per iteration) ----
        var counters = new ConnectionEventCounters();
        var metrics = new MetricsCollector();
        var observer = new CompositeConnectionObserver(counters, metrics);
        var factory = new TaskConnectionFactory(_options.Target, _connectionString, observer, _config.Client);
        var runner = new TaskRunner(factory, metrics, _options.Target, _config.TaskSleepMs, _config.Workload);

        // Per-host RNG seed offset: with independent seeds the Poisson superposition of M hosts is
        // Poisson(M·λ), so combined offered load scales with host count instead of M identical copies.
        var hostSeed = BmtConstants.DatasetSeed + _options.HostId;
        var reqIdRng = new Random(hostSeed);
        var reqIdLock = new object();
        string SelectReqId()
        {
            lock (reqIdLock)
            {
                return (reqIdRng.Next(1, (int)datasetCount + 1)).ToString();
            }
        }

        using var launcher = new TaskLauncher(runner, _config.Scenario.MaxConcurrentTasks, SelectReqId);

        var runClock = Stopwatch.StartNew();
        metrics.StartClock();
        await using var sampler = new ClientResourceSampler(_config.Scenario.ResourceSampleIntervalMs, runClock);
        sampler.Start();

        var startedUtc = DateTime.UtcNow;

        // ---- Execute the selected scenario(s) ----
        try
        {
            var generators = new List<Task>();
            var (runSteady, runBurst) = ResolveActiveScenarios();
            if (runSteady)
            {
                var steady = new SteadyScenario(_config.Scenario.Steady, effectiveDurationSec);
                generators.Add(steady.RunAsync(launcher, ct));
            }

            if (runBurst)
            {
                var burst = new BurstScenario(_config.Scenario.Burst, effectiveDurationSec, hostSeed);
                generators.Add(burst.RunAsync(launcher, ct));
            }

            await Task.WhenAll(generators).ConfigureAwait(false);
            ConsoleLog.Info("Arrival generation complete; draining in-flight Tasks...");
            await launcher.DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Warn("Iteration canceled; draining in-flight Tasks...");
            await launcher.DrainAsync().ConfigureAwait(false);
        }

        runClock.Stop();
        var finishedUtc = DateTime.UtcNow;
        await sampler.StopAsync().ConfigureAwait(false);

        // ---- Build result ----
        var result = metrics.Build(counters, sampler.Samples(), sampler.Peaks());
        result.Target = cliName;
        result.Scenario = _options.Scenario.ToString();
        result.HostId = _options.HostId;
        result.HostCount = _options.HostCount;
        result.RunTag = _options.RunTag;
        result.StartedUnixSeconds = ((DateTimeOffset)startedUtc).ToUnixTimeSeconds();
        result.WorkloadMode = _config.Workload.Token();
        result.IterationNumber = iterNumber;
        result.IterationCount = totalIters;
        result.StartedUtc = startedUtc.ToString("O");
        result.FinishedUtc = finishedUtc.ToString("O");
        result.DurationSeconds = Math.Round(runClock.Elapsed.TotalSeconds, 3);
        result.MaskedConnectionString = ConnectionStringMasker.Mask(_connectionString);
        result.TaskSleepMs = _config.TaskSleepMs;
        result.DatasetDocumentCount = datasetCount;
        result.Preflight = gate;

        // ---- Persist artifacts into iter-NN subfolder ----
        var iterLabel = $"iter-{iterNumber:D2}";
        var iterDir = Path.Combine(campaignDir, iterLabel);
        Directory.CreateDirectory(iterDir);

        var fileStamp = startedUtc.ToString("yyyyMMdd-HHmmss");
        var runId = $"{campaignId}-{iterLabel}-{fileStamp}";

        var jsonPath = Path.Combine(iterDir, runId + ".json");
        var tsPath = Path.Combine(iterDir, runId + "-timeseries.csv");
        var latPath = Path.Combine(iterDir, runId + "-latency.csv");

        await File.WriteAllTextAsync(jsonPath, result.ToJson(), ct).ConfigureAwait(false);
        await CsvWriter.WriteTimeSeriesAsync(result, tsPath, ct).ConfigureAwait(false);
        await CsvWriter.WriteLatencySummaryAsync(result, latPath, ct).ConfigureAwait(false);

        ConsoleLog.Info($"Wrote: {jsonPath}");
        ConsoleLog.Info($"Wrote: {tsPath}");
        ConsoleLog.Info($"Wrote: {latPath}");

        PrintIterationSummary(result);

        // Return relative path from campaign dir for the aggregate cross-reference.
        var relPath = Path.Combine(iterLabel, runId + ".json");
        return (result, relPath);
    }

    /// <summary>
    /// A scenario runs only when the CLI <c>--scenario</c> selects it AND its config <c>Enabled</c>
    /// flag is true. This lets a config keep Steady and Burst separate by enabling just one of them,
    /// even under the default <c>--scenario both</c>.
    /// </summary>
    private (bool runSteady, bool runBurst) ResolveActiveScenarios()
    {
        var runSteady = _options.Scenario is RunScenario.Steady or RunScenario.Both
                        && _config.Scenario.Steady.Enabled;
        var runBurst = _options.Scenario is RunScenario.Burst or RunScenario.Both
                       && _config.Scenario.Burst.Enabled;
        return (runSteady, runBurst);
    }

    /// <summary>
    /// When <c>--start-at</c> is supplied, block until that UTC instant so every host in a multi-host
    /// campaign begins its timed phase together. A past instant starts immediately (with a warning).
    /// </summary>
    private async Task WaitForCoordinatedStartAsync(CancellationToken ct)
    {
        if (_options.StartAtUtc is not { } startAt)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var wait = startAt - now;
        if (wait <= TimeSpan.Zero)
        {
            ConsoleLog.Warn($"--start-at {startAt:O} is in the past ({(-wait).TotalSeconds:F1}s ago); starting now. " +
                            "Combined concurrency across hosts may be misaligned.");
            return;
        }

        ConsoleLog.Info($"Coordinated start: waiting {wait.TotalSeconds:F1}s until {startAt:O} (host {_options.HostId}/{_options.HostCount})...");
        await Task.Delay(wait, ct).ConfigureAwait(false);
        ConsoleLog.Info("Coordinated start instant reached — beginning timed phase.");
    }

    private async Task<long> CountInputAsync(CancellationToken ct)
    {
        using var conn = new TaskConnectionFactory(_options.Target, _connectionString, tuning: _config.Client).Create();
        return await conn.CalcInput.CountDocumentsAsync(FilterDefinition<CalcInputDoc>.Empty, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Untimed warm-up sweep so the timed run starts against a warm data cache (§6.5 starting state).
    /// Reads a bounded sample of input docs by ReqId, then disposes the throwaway connection so NO
    /// connection is retained into the timed phase.
    /// </summary>
    private async Task WarmCacheAsync(long datasetCount, CancellationToken ct)
    {
        var sample = (int)Math.Min(datasetCount, _config.Preflight.SampleSize);
        ConsoleLog.Info($"Warming data cache (untimed): reading {sample} input docs by ReqId...");
        using var conn = new TaskConnectionFactory(_options.Target, _connectionString, tuning: _config.Client).Create();
        var step = Math.Max(1, datasetCount / Math.Max(1, sample));
        var read = 0;
        for (long id = 1; id <= datasetCount && read < sample; id += step, read++)
        {
            ct.ThrowIfCancellationRequested();
            var filter = Builders<CalcInputDoc>.Filter.Eq(d => d.ReqId, id.ToString());
            await conn.CalcInput.Find(filter).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        ConsoleLog.Info("Warm-up complete (throwaway connection disposed; none retained).");
    }

    // Short database label for compact folder names.
    private static string DbLabel(string cliName) => cliName switch
    {
        "mongo-shard" => "mongo",
        "mongo-vm" => "mongovm",
        "documentdb" => "docdb",
        "cosmos-ru" => "cosmos",
        _ => cliName,
    };

    // open  = open-loop (arrival-rate driven; concurrency is unbounded, "max open connections").
    // closed = closed-loop (a fixed task/concurrency ceiling gates arrivals — the task-perspective test).
    // Burst carries the explicit OpenLoop switch; a steady-only run is arrival-rate driven (open).
    private string LoopLabel()
    {
        var (_, runBurst) = ResolveActiveScenarios();
        return runBurst ? (_config.Scenario.Burst.OpenLoop ? "open" : "closed") : "open";
    }

    // full = full 4-op cycle; query = single-op find; insert = single-op insert.
    private string WorkloadLabel() => _config.Workload.Mode switch
    {
        WorkloadMode.FullWorkload => "full",
        WorkloadMode.SingleOp => _config.Workload.SingleOpType switch
        {
            SingleOpType.FindInput => "query",
            SingleOpType.InsertOutput => "insert",
            _ => "op",
        },
        _ => "full",
    };

    // Base-36 encode, returning at most the last <maxChars> characters — a compact, unique-per-run stamp.
    private static string Base36Suffix(long value, int maxChars)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value <= 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, chars[(int)(value % 36)]);
            value /= 36;
        }

        var s = sb.ToString();
        return s.Length <= maxChars ? s : s[^maxChars..];
    }

    private static void PrintIterationSummary(RunResult r)
    {
        ConsoleLog.Info(new string('-', 70));
        ConsoleLog.Info($"ITER {r.IterationNumber}/{r.IterationCount} DONE: {r.Target} / {r.Scenario} / {r.WorkloadMode} / {r.DurationSeconds}s");
        ConsoleLog.Info($"Tasks: {r.Totals.TotalTasks} total, {r.Totals.SuccessfulTasks} ok, {r.Totals.FailedTasks} failed.");
        ConsoleLog.Info($"Ops:   {r.Totals.TotalOps} total, {r.Totals.FailedOps} failed.");
        var cyc = r.TaskCycleLatencyMs;
        ConsoleLog.Info($"Cycle latency ms: p50={cyc.P50Ms:F1} p95={cyc.P95Ms:F1} p99={cyc.P99Ms:F1} p99.9={cyc.P999Ms:F1}");
        foreach (var op in OpNames.Ordered)
        {
            if (r.OperationLatencyMs.TryGetValue(op, out var s) && s.Count > 0)
            {
                ConsoleLog.Info($"  {op,-12} ms: p50={s.P50Ms:F1} p95={s.P95Ms:F1} p99={s.P99Ms:F1} p99.9={s.P999Ms:F1} (n={s.Count})");
            }
        }

        ConsoleLog.Info($"Connections: created={r.Connections.Created} closed={r.Connections.Closed} " +
                        $"(created/task={r.Connections.CreatedToTaskRatio:F3}). " +
                        $"No-reuse confirmed: {r.ReuseCheck.NoReuseConfirmed}.");
        if (r.ErrorsByType.Count > 0)
        {
            ConsoleLog.Warn("Errors by type: " + string.Join(", ", r.ErrorsByType.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        ConsoleLog.Info($"Client peaks: ports={r.Process.PeakEphemeralPortsInUse} time_wait={r.Process.PeakTimeWaitSockets} " +
                        $"handles={r.Process.PeakHandleCount} cpu%={r.Process.MaxCpuPercent:F1} ws={r.Process.PeakWorkingSetBytes / (1024 * 1024)}MB");
    }

    private static void PrintAggregateSummary(AggregateResult agg)
    {
        ConsoleLog.Info(new string('=', 70));
        ConsoleLog.Info($"AGGREGATE: {agg.Target} / {agg.Scenario} / {agg.WorkloadMode} / {agg.IterationCount} iterations");
        ConsoleLog.Info($"Mean tasks/s (successful): {agg.Stats.MeanSuccessfulTasksPerSec:F1}  Mean error%: {agg.Stats.MeanErrorRatePct:F2}%");
        var c = agg.Stats.TaskCycleMs;
        ConsoleLog.Info($"Cycle p99 ms: mean={c.MeanP99Ms:F1} min={c.MinP99Ms:F1} max={c.MaxP99Ms:F1}");
        foreach (var kv in agg.Stats.OperationMs)
        {
            if (kv.Value.TotalCount > 0)
            {
                ConsoleLog.Info($"  {kv.Key,-12} p99 ms: mean={kv.Value.MeanP99Ms:F1} min={kv.Value.MinP99Ms:F1} max={kv.Value.MaxP99Ms:F1}");
            }
        }
    }
}
