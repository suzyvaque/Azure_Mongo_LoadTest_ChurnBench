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

        // Coordinator-driven mode: when --iteration-number is supplied, the central coordinator
        // (Invoke-Campaign.ps1) owns the iteration loop and re-computes a fresh shared --start-at per
        // iteration, so this invocation runs EXACTLY ONE iteration stamped with the given number. In
        // local mode (no --iteration-number) the orchestrator runs the config's Scenario.Iterations.
        var coordinatorDriven = _options.IterationNumber > 0;
        var iterations = coordinatorDriven ? 1 : _config.Scenario.Iterations;

        ConsoleLog.Info($"=== LoadGen run: target={cliName} scenario={_options.Scenario} " +
                        $"workload={workloadToken} iterations={(coordinatorDriven ? $"1 (coordinator iteration {_options.IterationNumber})" : iterations.ToString())} ===");
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

            // In coordinator-driven mode the label is the coordinator's iteration index (out of the
            // planned total); locally it is the loop index out of the config's iteration count.
            var iterNumber = coordinatorDriven ? _options.IterationNumber : iter;
            var iterTotal = coordinatorDriven
                ? (_options.IterationCount > 0 ? _options.IterationCount : _options.IterationNumber)
                : iterations;

            ConsoleLog.Info($"");
            ConsoleLog.Info($">>> Iteration {iterNumber}/{iterTotal} <<<");

            var (result, relPath) = await RunIterationAsync(
                iterNumber, iterTotal, campaignId, campaignDir,
                datasetCount, effectiveDurationSec, gate, cliName, ct).ConfigureAwait(false);

            iterResults.Add(result);
            artifactRelPaths.Add(relPath);
        }

        // ---- Write aggregate ----
        // Skip the per-host cross-iteration aggregate in coordinator-driven mode: each invocation is a
        // single iteration, and the authoritative cross-iteration mean/min/max is produced by the
        // coordinator's `report merge` after every host's iteration has been validated (§5).
        if (iterResults.Count > 0 && !coordinatorDriven)
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
        metrics.BindConnectionCounters(counters);
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

        // §4 target-endpoint resolution: resolve the database destination IP/port set BEFORE the timed
        // clocks start (so slow DNS/SRV never inflates run duration or shifts per-second indices), letting
        // the TCP sampler filter to TARGET sockets only (host-wide totals cannot prove DB-specific behavior).
        var endpointResolver = new TargetEndpointResolver(_connectionString);
        await endpointResolver.RefreshAsync(ct).ConfigureAwait(false);
        ConsoleLog.Info($"[iter {iterNumber}] target endpoints ({endpointResolver.EndpointCount}): " +
                        (endpointResolver.EndpointCount == 0
                            ? "(none resolved — target TCP telemetry will be empty)"
                            : string.Join(", ", endpointResolver.EndpointDescriptions)));

        var runClock = Stopwatch.StartNew();
        metrics.StartClock();

        await using var sampler = new ClientResourceSampler(
            _config.Scenario.ResourceSampleIntervalMs, runClock, endpointResolver);
        sampler.Start();

        var startedUtc = DateTime.UtcNow;
        var arrivalStoppedUtc = startedUtc;
        var drainStartedUtc = startedUtc;
        long connectionsReadyAtArrivalStop = 0;

        // ---- Execute the selected scenario(s) under the explicit §2 arrival→drain model ----
        //   arrival: generators schedule new Tasks for the configured window
        //   arrival stop: generators complete; snapshot outstanding backlog; NO new Tasks scheduled
        //   drain: all Tasks scheduled during arrival are allowed to finish
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
            arrivalStoppedUtc = DateTime.UtcNow;
            metrics.OnArrivalStopped();
            // Concurrent driver-ready connections at arrival stop (authoritative ActiveReady gauge, §3).
            connectionsReadyAtArrivalStop = counters.ActiveReady;
            drainStartedUtc = arrivalStoppedUtc;
            ConsoleLog.Info($"Arrival generation complete after {(arrivalStoppedUtc - startedUtc).TotalSeconds:F1}s; " +
                            $"draining {metrics.TasksOutstandingAtArrivalStop} outstanding Task(s)...");
            await launcher.DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            arrivalStoppedUtc = DateTime.UtcNow;
            metrics.OnArrivalStopped();
            connectionsReadyAtArrivalStop = counters.ActiveReady;
            drainStartedUtc = arrivalStoppedUtc;
            ConsoleLog.Warn("Iteration canceled; draining in-flight Tasks...");
            await launcher.DrainAsync().ConfigureAwait(false);
        }

        runClock.Stop();
        var finishedUtc = DateTime.UtcNow;
        await sampler.StopAsync().ConfigureAwait(false);

        // ---- Build result ----
        var result = metrics.Build(counters, sampler.Samples(), sampler.Peaks());
        result.TargetTcpSamples = sampler.TargetTcpSamples().ToList();
        result.TargetTcp = sampler.TargetTcpInfo();
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

        // ---- §2 arrival/drain model + open-loop rates (denominator = the 300s ARRIVAL window, NOT the
        //      total iteration duration, so offered load is not understated by drain time). ----
        var arrivalWindowSec = IntendedArrivalDurationSeconds(effectiveDurationSec);
        result.Arrival = new ArrivalDrainStats
        {
            ArrivalStartedUtc = startedUtc.ToString("O"),
            ArrivalStoppedUtc = arrivalStoppedUtc.ToString("O"),
            ArrivalDurationSeconds = arrivalWindowSec,
            MeasuredArrivalDurationSeconds = Math.Round((arrivalStoppedUtc - startedUtc).TotalSeconds, 3),
            DrainStartedUtc = drainStartedUtc.ToString("O"),
            DrainFinishedUtc = finishedUtc.ToString("O"),
            DrainDurationSeconds = Math.Round((finishedUtc - drainStartedUtc).TotalSeconds, 3),
            TotalIterationDurationSeconds = Math.Round((finishedUtc - startedUtc).TotalSeconds, 3),
            TasksOutstandingAtArrivalStop = metrics.TasksOutstandingAtArrivalStop,
            InFlightAtArrivalStop = metrics.InFlightAtArrivalStop,
            ConnectionsReadyAtArrivalStop = connectionsReadyAtArrivalStop,
            MaximumDrainBacklog = metrics.MaximumDrainBacklog,
        };
        result.OpenLoop.ArrivalWindowSeconds = arrivalWindowSec;
        result.OpenLoop.ScheduledTasksPerSec = arrivalWindowSec > 0
            ? Math.Round((double)result.Totals.TasksScheduled / arrivalWindowSec, 2)
            : 0;
        result.OpenLoop.StartedTasksPerSec = arrivalWindowSec > 0
            ? Math.Round((double)result.Totals.TasksStarted / arrivalWindowSec, 2)
            : 0;

        // ---- Persist artifacts into iter-NN subfolder ----
        var iterLabel = $"iter-{iterNumber:D2}";
        var iterDir = Path.Combine(campaignDir, iterLabel);
        Directory.CreateDirectory(iterDir);

        var fileStamp = startedUtc.ToString("yyyyMMdd-HHmmss");
        var runId = $"{campaignId}-{iterLabel}-{fileStamp}";

        var jsonPath = Path.Combine(iterDir, runId + ".json");
        var tsPath = Path.Combine(iterDir, runId + "-timeseries.csv");
        var latPath = Path.Combine(iterDir, runId + "-latency.csv");
        var tcpPath = Path.Combine(iterDir, runId + "-target-tcp.csv");

        await File.WriteAllTextAsync(jsonPath, result.ToJson(), ct).ConfigureAwait(false);
        await CsvWriter.WriteTimeSeriesAsync(result, tsPath, ct).ConfigureAwait(false);
        await CsvWriter.WriteLatencySummaryAsync(result, latPath, ct).ConfigureAwait(false);
        await CsvWriter.WriteTargetTcpAsync(result, tcpPath, ct).ConfigureAwait(false);

        ConsoleLog.Info($"Wrote: {jsonPath}");
        ConsoleLog.Info($"Wrote: {tsPath}");
        ConsoleLog.Info($"Wrote: {latPath}");
        ConsoleLog.Info($"Wrote: {tcpPath}");

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
    /// The INTENDED arrival-window length (seconds) used as the authoritative denominator for open-loop
    /// rates (§2). Generators schedule new Tasks for exactly this long; drain time is excluded. Priority:
    /// CLI/config effective duration override, else the longest active scenario's configured duration.
    /// </summary>
    private int IntendedArrivalDurationSeconds(int effectiveDurationSec)
    {
        if (effectiveDurationSec > 0)
        {
            return effectiveDurationSec;
        }

        var (runSteady, runBurst) = ResolveActiveScenarios();
        var seconds = 0;
        if (runSteady)
        {
            seconds = Math.Max(seconds, _config.Scenario.Steady.DurationSeconds);
        }

        if (runBurst)
        {
            seconds = Math.Max(seconds, _config.Scenario.Burst.DurationSeconds);
        }

        return seconds;
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
        ConsoleLog.Info($"Tasks: scheduled={r.Totals.TasksScheduled} started={r.Totals.TasksStarted} " +
                        $"ok={r.Totals.SuccessfulTasks} failed={r.Totals.FailedTasks} " +
                        $"(peak sched-backlog={r.Totals.PeakScheduledNotStartedBacklog}).");
        ConsoleLog.Info($"Arrival: window={r.OpenLoop.ArrivalWindowSeconds:F0}s scheduled/s={r.OpenLoop.ScheduledTasksPerSec:F1} " +
                        $"started/s={r.OpenLoop.StartedTasksPerSec:F1} completed-in-arrival={r.OpenLoop.TasksCompletedDuringArrival}.");
        ConsoleLog.Info($"Drain: outstanding@stop={r.Arrival.TasksOutstandingAtArrivalStop} max-backlog={r.Arrival.MaximumDrainBacklog} " +
                        $"drain={r.Arrival.DrainDurationSeconds:F1}s total-iter={r.Arrival.TotalIterationDurationSeconds:F1}s.");
        var sched = r.OpenLoop.SchedulerQueueLatencyMs;
        var exec = r.OpenLoop.TaskExecutionLatencyMs;
        var e2e = r.OpenLoop.OfferedToFinishedLatencyMs;
        ConsoleLog.Info($"Scheduler-queue ms: p50={sched.P50Ms:F1} p95={sched.P95Ms:F1} p99={sched.P99Ms:F1}");
        ConsoleLog.Info($"Execution ms:       p50={exec.P50Ms:F1} p95={exec.P95Ms:F1} p99={exec.P99Ms:F1}");
        ConsoleLog.Info($"Offered→finished ms (authoritative): p50={e2e.P50Ms:F1} p95={e2e.P95Ms:F1} p99={e2e.P99Ms:F1}");
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
        var lc = r.Lifecycle;
        ConsoleLog.Info($"Lifecycle: created={lc.ConnectionsCreated} ready={lc.ConnectionsReady} failed={lc.ConnectionsFailed} " +
                        $"closed={lc.ConnectionsClosed} | peak connecting={lc.PeakActiveConnecting} ready={lc.PeakActiveReady} " +
                        $"waiting-server={lc.PeakWaitingForServer} | reconciled={lc.LifecycleReconciled} (created-closed={lc.CreatedMinusClosed}).");
        ConsoleLog.Info($"Cold-conn ms: demand→ready p50={lc.DemandToReadyLatencyMs.P50Ms:F1} p95={lc.DemandToReadyLatencyMs.P95Ms:F1} " +
                        $"p99={lc.DemandToReadyLatencyMs.P99Ms:F1} | driver-open p50={lc.DriverOpenLatencyMs.P50Ms:F1} " +
                        $"p95={lc.DriverOpenLatencyMs.P95Ms:F1} p99={lc.DriverOpenLatencyMs.P99Ms:F1}");
        if (r.ErrorsByType.Count > 0)
        {
            ConsoleLog.Warn("Errors by type: " + string.Join(", ", r.ErrorsByType.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        ConsoleLog.Info($"Client peaks: ports={r.Process.PeakEphemeralPortsInUse} time_wait={r.Process.PeakTimeWaitSockets} " +
                        $"handles={r.Process.PeakHandleCount} cpu%={r.Process.MaxCpuPercent:F1} ws={r.Process.PeakWorkingSetBytes / (1024 * 1024)}MB");
        var tcp = r.TargetTcp;
        ConsoleLog.Info($"Target TCP ({tcp.EndpointCount} endpoint(s), {r.TargetTcpSamples.Count} samples, dropped={tcp.DroppedSamples}): " +
                        $"peak established={tcp.PeakTargetEstablished} syn_sent={tcp.PeakTargetSynSent} time_wait={tcp.PeakTargetTimeWait} " +
                        $"close_wait={tcp.PeakTargetCloseWait} sockets={tcp.PeakTargetTotalSockets} local_ports={tcp.PeakTargetDistinctLocalPorts} | " +
                        $"host sockets={tcp.PeakHostTotalTcpSockets} ephemeral_util={tcp.PeakEphemeralUtilizationPct:F1}%");
    }

    private static void PrintAggregateSummary(AggregateResult agg)
    {
        ConsoleLog.Info(new string('=', 70));
        ConsoleLog.Info($"AGGREGATE: {agg.Target} / {agg.Scenario} / {agg.WorkloadMode} / {agg.IterationCount} iterations");
        ConsoleLog.Info($"Mean offered/s: {agg.Stats.MeanScheduledTasksPerSec:F1}  Mean successful/s: {agg.Stats.MeanSuccessfulTasksPerSec:F1}  " +
                        $"Mean error%: {agg.Stats.MeanErrorRatePct:F2}%  Mean drain: {agg.Stats.MeanDrainDurationSeconds:F1}s");
        var e2e = agg.Stats.OfferedToFinishedMs;
        ConsoleLog.Info($"True offered→finished p99 ms: mean={e2e.MeanP99Ms:F1} min={e2e.MinP99Ms:F1} max={e2e.MaxP99Ms:F1}");
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
