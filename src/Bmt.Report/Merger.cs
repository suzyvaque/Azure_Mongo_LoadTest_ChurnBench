using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bmt.Core.Metrics;

namespace Bmt.Report;

/// <summary>
/// Cross-host aggregator for a coordinated multi-host open-loop burst (test_instruction.md §6.2).
/// A single generator cannot reach ≥11,000 concurrent / ≥1,200 conn/s without hitting client-side
/// port/CPU limits, so the campaign is split across several co-located hosts. This merger unions each
/// host's per-second throughput series on the ABSOLUTE wall-clock second
/// (<see cref="RunResult.StartedUnixSeconds"/> + <see cref="ThroughputPoint.Second"/>), sums the
/// per-host connection-open rate and in-flight concurrency, and reports the combined peaks against the
/// ≥1,200 conn/s and ≥11,000 concurrent targets — the evidence that the envelope was actually reached.
/// </summary>
public static class Merger
{
    public static MergeReport Merge(
        string inputDir,
        string? runTagFilter,
        int concurrentTarget,
        int churnTarget)
    {
        if (!Directory.Exists(inputDir))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {inputDir}");
        }

        var runs = new List<RunResult>();
        foreach (var path in Directory.EnumerateFiles(inputDir, "*.json", SearchOption.AllDirectories))
        {
            RunResult? run = null;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!LooksLikeRun(doc.RootElement))
                {
                    continue;
                }

                run = RunResult.FromJson(json);
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn($"Skipping unreadable/again malformed JSON '{Path.GetFileName(path)}': {ex.Message}");
                continue;
            }

            if (!string.IsNullOrEmpty(runTagFilter) &&
                !string.Equals(run.RunTag, runTagFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            runs.Add(run);
        }

        var report = new MergeReport
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            InputDirectory = Path.GetFullPath(inputDir),
            RunTagFilter = runTagFilter ?? string.Empty,
            ConcurrentTarget = concurrentTarget,
            ChurnTarget = churnTarget,
        };

        foreach (var group in runs
                     .GroupBy(r => $"{r.Target}|{r.Scenario}|{r.IterationNumber}", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mg = BuildGroup(group.ToList());
            mg.ReachedChurnTarget = mg.PeakCombinedConnPerSec >= churnTarget;
            mg.ReachedReadyChurnTarget = mg.PeakCombinedReadyPerSec >= churnTarget;
            mg.ReachedActiveReadyTarget = mg.PeakCombinedActiveReady >= concurrentTarget;
            // The AUTHORITATIVE established-concurrency verdict is driver ActiveReady, NOT in-flight Task
            // count (§3: in-flight Tasks are not proof of established concurrent connections).
            mg.ReachedConcurrentTarget = mg.PeakCombinedActiveReady >= concurrentTarget;
            report.Groups.Add(mg);
        }

        report.CrossIteration = CrossIterationSummary.Build(report.Groups);
        return report;
    }

    private static MergeGroup BuildGroup(IReadOnlyList<RunResult> allHostRuns)
    {
        // Retries re-run the FULL three-host iteration, so a host can have artifacts from MORE THAN ONE
        // attempt sharing this iteration number (the coordinator preserves failed-attempt artifacts).
        // Keep only the LATEST attempt per host (highest StartedUnixSeconds): the coordinator declares an
        // iteration complete only when every host succeeded in the SAME (latest) attempt, so latest-per-
        // host reconstructs exactly that complete attempt and discards superseded partial artifacts.
        var retriedHostIds = allHostRuns
            .GroupBy(r => r.HostId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(x => x)
            .ToList();
        var hostRuns = allHostRuns
            .GroupBy(r => r.HostId)
            .Select(g => g.OrderByDescending(r => r.StartedUnixSeconds).First())
            .OrderBy(r => r.HostId)
            .ToList();
        var supersededRuns = allHostRuns.Count - hostRuns.Count;

        // Absolute wall-clock second -> summed connection-open rate + summed in-flight concurrency.
        var connBySecond = new Dictionary<long, long>();
        var readyBySecond = new Dictionary<long, long>();
        var activeReadyBySecond = new Dictionary<long, long>();
        var inFlightBySecond = new Dictionary<long, long>();
        var opsBySecond = new Dictionary<long, long>();
        var failedBySecond = new Dictionary<long, long>();

        foreach (var run in hostRuns)
        {
            foreach (var p in run.Throughput)
            {
                var sec = run.StartedUnixSeconds + p.Second;
                connBySecond[sec] = connBySecond.GetValueOrDefault(sec) + p.ConnectionsCreated;
                readyBySecond[sec] = readyBySecond.GetValueOrDefault(sec) + p.ConnectionsReady;
                activeReadyBySecond[sec] = activeReadyBySecond.GetValueOrDefault(sec) + p.ActiveReady;
                inFlightBySecond[sec] = inFlightBySecond.GetValueOrDefault(sec) + p.InFlightTasks;
                opsBySecond[sec] = opsBySecond.GetValueOrDefault(sec) + p.CombinedOps;
                failedBySecond[sec] = failedBySecond.GetValueOrDefault(sec) + p.FailedOps;
            }
        }

        var first = hostRuns[0];
        var hostIds = hostRuns.Select(r => r.HostId).OrderBy(x => x).ToList();
        var declaredHostCount = hostRuns.Max(r => r.HostCount);

        // A synchronized iteration requires EXACTLY the declared host set (§1: host IDs 1..N present, no
        // more, no fewer) AND a consistent HostCount across every host's artifact. Any missing required
        // host, any unexpected host ID, or an inconsistent declared host count invalidates the iteration
        // (so a two-host set — e.g. one host silently absent — is never accepted as complete).
        var requiredHostIds = Enumerable.Range(1, Math.Max(1, declaredHostCount)).ToList();
        var missingHostIds = requiredHostIds.Where(id => !hostIds.Contains(id)).ToList();
        var unexpectedHostIds = hostIds.Where(id => !requiredHostIds.Contains(id)).ToList();
        var hostCountConsistent = hostRuns.Select(r => r.HostCount).Distinct().Count() == 1;

        // Actual start-time skew across the hosts of this iteration (§1: report it). Under a correct
        // coordinator-owned loop every host starts at the SAME shared --start-at instant, so this is ~0.
        var startSeconds = hostRuns.Select(r => r.StartedUnixSeconds).ToList();
        var skewSeconds = startSeconds.Count == 0 ? 0 : startSeconds.Max() - startSeconds.Min();

        var peakConn = connBySecond.Count == 0 ? 0 : connBySecond.Values.Max();
        var peakConc = inFlightBySecond.Count == 0 ? 0 : inFlightBySecond.Values.Max();
        var peakReady = readyBySecond.Count == 0 ? 0 : readyBySecond.Values.Max();
        var peakActiveReady = activeReadyBySecond.Count == 0 ? 0 : activeReadyBySecond.Values.Max();

        // §5 per-host detail + combined generator-fidelity / drain / true-e2e / failure for this iteration.
        var perHost = hostRuns
            .OrderBy(r => r.HostId)
            .Select(r => new MergeHostSummary
            {
                HostId = r.HostId,
                StartedUnixSeconds = r.StartedUnixSeconds,
                TasksScheduled = r.Totals.TasksScheduled,
                TasksStarted = r.Totals.TasksStarted,
                ScheduledTasksPerSec = r.OpenLoop.ScheduledTasksPerSec,
                StartedTasksPerSec = r.OpenLoop.StartedTasksPerSec,
                FailedTasks = r.Totals.FailedTasks,
                SchedulerLatencyP99Ms = r.OpenLoop.SchedulerQueueLatencyMs.P99Ms,
                TrueE2eP99Ms = r.OpenLoop.OfferedToFinishedLatencyMs.P99Ms,
                DemandToReadyP99Ms = r.Lifecycle.DemandToReadyLatencyMs.P99Ms,
                PeakActiveReady = r.Lifecycle.PeakActiveReady,
                DrainDurationSeconds = r.Arrival.DrainDurationSeconds,
                MaximumDrainBacklog = r.Arrival.MaximumDrainBacklog,
                LifecycleReconciled = r.Lifecycle.LifecycleReconciled,
            })
            .ToList();

        var totalTasks = hostRuns.Sum(r => r.Totals.TotalTasks);
        var totalFailed = hostRuns.Sum(r => r.Totals.FailedTasks);

        var group = new MergeGroup
        {
            Target = first.Target,
            Scenario = first.Scenario,
            WorkloadMode = first.WorkloadMode,
            RunTag = first.RunTag,
            IterationNumber = first.IterationNumber,
            IterationCount = first.IterationCount,
            HostsFound = hostIds.Count,
            DeclaredHostCount = declaredHostCount,
            HostIds = hostIds,
            RequiredHostIds = requiredHostIds,
            MissingHostIds = missingHostIds,
            UnexpectedHostIds = unexpectedHostIds,
            RetriedHostIds = retriedHostIds,
            SupersededRuns = supersededRuns,
            HostCountConsistent = hostCountConsistent,
            StartSkewSeconds = skewSeconds,
            Valid = missingHostIds.Count == 0 && unexpectedHostIds.Count == 0 && hostCountConsistent,
            PeakCombinedConnPerSec = peakConn,
            PeakCombinedReadyPerSec = peakReady,
            PeakCombinedActiveReady = peakActiveReady,
            PeakCombinedInFlight = peakConc,
            TotalConnectionsCreated = connBySecond.Values.Sum(),
            CombinedWindowSeconds = connBySecond.Count,
            // Combined generator fidelity = SUM of per-host offered/started rates (the campaign injects the
            // Poisson superposition of all hosts). Failure rate is over all Tasks; true-e2e p99 and drain
            // take the WORST (max) across hosts since digests can't be merged and the slowest host bounds
            // the synchronized iteration.
            CombinedOfferedTasksPerSec = Math.Round(hostRuns.Sum(r => r.OpenLoop.ScheduledTasksPerSec), 2),
            CombinedStartedTasksPerSec = Math.Round(hostRuns.Sum(r => r.OpenLoop.StartedTasksPerSec), 2),
            FailureRatePct = totalTasks == 0 ? 0 : Math.Round(100.0 * totalFailed / totalTasks, 3),
            TrueE2eP99Ms = perHost.Count == 0 ? 0 : perHost.Max(h => h.TrueE2eP99Ms),
            DrainDurationSeconds = perHost.Count == 0 ? 0 : perHost.Max(h => h.DrainDurationSeconds),
            AllHostsReconciled = perHost.All(h => h.LifecycleReconciled),
            Hosts = perHost,
        };

        // Combined per-second series, ordered by wall-clock second (relative to the earliest second).
        var minSecond = connBySecond.Count == 0 ? 0 : connBySecond.Keys.Min();
        foreach (var sec in connBySecond.Keys.OrderBy(x => x))
        {
            group.Series.Add(new MergeSecond
            {
                WallClockUnixSecond = sec,
                RelativeSecond = (int)(sec - minSecond),
                CombinedConnPerSec = connBySecond[sec],
                CombinedInFlight = inFlightBySecond.GetValueOrDefault(sec),
                CombinedOps = opsBySecond.GetValueOrDefault(sec),
                CombinedFailedOps = failedBySecond.GetValueOrDefault(sec),
            });
        }

        return group;
    }

    /// <summary>Write the combined per-second series for one group to CSV (for spreadsheets/graphs).</summary>
    public static void WriteGroupCsv(MergeGroup group, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("relative_second,wall_clock_unix_second,combined_conn_per_sec,combined_in_flight,combined_ops,combined_failed_ops");
        foreach (var s in group.Series)
        {
            sb.Append(s.RelativeSecond).Append(',')
              .Append(s.WallClockUnixSecond).Append(',')
              .Append(s.CombinedConnPerSec).Append(',')
              .Append(s.CombinedInFlight).Append(',')
              .Append(s.CombinedOps).Append(',')
              .Append(s.CombinedFailedOps.ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static bool LooksLikeRun(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Totals", out _) &&
        root.TryGetProperty("Throughput", out _);
}

/// <summary>Top-level cross-host merge result, serialized to the <c>merge</c> command's output JSON.</summary>
public sealed class MergeReport
{
    public string GeneratedUtc { get; set; } = string.Empty;

    public string InputDirectory { get; set; } = string.Empty;

    public string RunTagFilter { get; set; } = string.Empty;

    public int ConcurrentTarget { get; set; }

    public int ChurnTarget { get; set; }

    public List<MergeGroup> Groups { get; set; } = new();

    /// <summary>
    /// Cross-iteration mean/min/max across the SYNCHRONIZED iterations, computed only over iterations
    /// that passed independent validation (all required hosts present, §1/§5).
    /// </summary>
    public CrossIterationSummary CrossIteration { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Combined result for one (target, scenario) across all its hosts.</summary>
public sealed class MergeGroup
{
    public string Target { get; set; } = string.Empty;

    public string Scenario { get; set; } = string.Empty;

    public string WorkloadMode { get; set; } = string.Empty;

    public string RunTag { get; set; } = string.Empty;

    /// <summary>1-based synchronized-iteration number this group aggregates (§1: never mix iterations).</summary>
    public int IterationNumber { get; set; }

    /// <summary>Total iterations the coordinator planned (from the hosts' artifacts), for gap detection.</summary>
    public int IterationCount { get; set; }

    public int HostsFound { get; set; }

    public int DeclaredHostCount { get; set; }

    public List<int> HostIds { get; set; } = new();

    /// <summary>Host IDs that MUST be present for a valid synchronized iteration (1..DeclaredHostCount).</summary>
    public List<int> RequiredHostIds { get; set; } = new();

    /// <summary>Required host IDs that did NOT report for this iteration (empty when valid).</summary>
    public List<int> MissingHostIds { get; set; } = new();

    /// <summary>Host IDs present that are OUTSIDE the required 1..N set (a merge-integrity fault).</summary>
    public List<int> UnexpectedHostIds { get; set; } = new();

    /// <summary>
    /// Host IDs that had more than one attempt's artifact for this iteration (informational). Retries
    /// re-run the full iteration; the LATEST attempt per host is used and earlier ones are superseded —
    /// this is expected after a rerun and does NOT invalidate the iteration.
    /// </summary>
    public List<int> RetriedHostIds { get; set; } = new();

    /// <summary>Number of superseded (older-attempt) artifacts dropped during latest-per-host dedupe.</summary>
    public int SupersededRuns { get; set; }

    /// <summary>True when every host's artifact declared the same HostCount (an inconsistency is a fault).</summary>
    public bool HostCountConsistent { get; set; }

    /// <summary>Actual start-time skew (seconds) between the earliest and latest host in this iteration.</summary>
    public long StartSkewSeconds { get; set; }

    /// <summary>
    /// True only when the iteration's LATEST-attempt host set is EXACTLY the required 1..N set (no
    /// missing host, no unexpected host ID) with a consistent declared host count. Invalid iterations
    /// are still emitted (with the reason) but are EXCLUDED from the cross-iteration summary (§1/§5:
    /// do not continue with only two hosts; do not merge incomplete iterations).
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>Peak of the per-second SUM of per-host connections opened — the combined conn/s spike.</summary>
    public long PeakCombinedConnPerSec { get; set; }

    /// <summary>Peak of the per-second SUM of per-host connections that became driver-READY — combined ready/s (§3).</summary>
    public long PeakCombinedReadyPerSec { get; set; }

    /// <summary>Peak of the per-second SUM of per-host concurrent Ready connections — combined ActiveReady (§3 ≥ 11,000).</summary>
    public long PeakCombinedActiveReady { get; set; }

    /// <summary>
    /// Peak of the per-second SUM of per-host in-flight Tasks — an upper-bound estimate of combined
    /// concurrency (per-host per-second maxima may occur at slightly different sub-second offsets).
    /// Corroborate with server-side connection counts for the authoritative peak.
    /// </summary>
    public long PeakCombinedInFlight { get; set; }

    public long TotalConnectionsCreated { get; set; }

    public int CombinedWindowSeconds { get; set; }

    /// <summary>§5: combined offered Tasks/s = sum of per-host offered rates (arrival-window denominator).</summary>
    public double CombinedOfferedTasksPerSec { get; set; }

    /// <summary>§5: combined started Tasks/s = sum of per-host started rates.</summary>
    public double CombinedStartedTasksPerSec { get; set; }

    /// <summary>§5: failure rate (%) across all hosts' Tasks this iteration.</summary>
    public double FailureRatePct { get; set; }

    /// <summary>§5: true offered-to-finished p99 (ms) — the WORST host bounds the synchronized iteration.</summary>
    public double TrueE2eP99Ms { get; set; }

    /// <summary>§5: drain duration (s) — the WORST (longest-draining) host bounds the iteration.</summary>
    public double DrainDurationSeconds { get; set; }

    /// <summary>§5: true only when every host's connection lifecycle reconciled (created≈closed).</summary>
    public bool AllHostsReconciled { get; set; }

    /// <summary>§5: per-host breakdown for this iteration (generator fidelity / lifecycle / drain).</summary>
    public List<MergeHostSummary> Hosts { get; set; } = new();

    /// <summary>True when the combined conn/s peak met the campaign churn target (default ≥ 1,200).</summary>
    public bool ReachedChurnTarget { get; set; }

    /// <summary>True when the combined driver-READY/s peak met the churn target — the stronger §3 evidence.</summary>
    public bool ReachedReadyChurnTarget { get; set; }

    /// <summary>True when the combined concurrency peak met the campaign concurrent target (default ≥ 11,000).</summary>
    public bool ReachedConcurrentTarget { get; set; }

    /// <summary>True when the combined concurrent ACTIVE-READY peak met the concurrent target — the §3 evidence.</summary>
    public bool ReachedActiveReadyTarget { get; set; }

    public List<MergeSecond> Series { get; set; } = new();
}

/// <summary>One wall-clock second of the combined cross-host series.</summary>
public sealed class MergeSecond
{
    public long WallClockUnixSecond { get; set; }

    public int RelativeSecond { get; set; }

    public long CombinedConnPerSec { get; set; }

    public long CombinedInFlight { get; set; }

    public long CombinedOps { get; set; }

    public long CombinedFailedOps { get; set; }
}

/// <summary>§5 per-host breakdown within one synchronized iteration (generator fidelity / lifecycle / drain).</summary>
public sealed class MergeHostSummary
{
    public int HostId { get; set; }

    public long StartedUnixSeconds { get; set; }

    public long TasksScheduled { get; set; }

    public long TasksStarted { get; set; }

    public double ScheduledTasksPerSec { get; set; }

    public double StartedTasksPerSec { get; set; }

    public long FailedTasks { get; set; }

    public double SchedulerLatencyP99Ms { get; set; }

    public double TrueE2eP99Ms { get; set; }

    public double DemandToReadyP99Ms { get; set; }

    public int PeakActiveReady { get; set; }

    public double DrainDurationSeconds { get; set; }

    public long MaximumDrainBacklog { get; set; }

    public bool LifecycleReconciled { get; set; }
}

/// <summary>
/// Cross-iteration mean/min/max over the VALID synchronized iterations (§5). Computed per
/// (target, scenario) only after each iteration has passed independent host-completeness validation,
/// so an incomplete iteration never contaminates the campaign-level summary.
/// </summary>
public sealed class CrossIterationSummary
{
    public List<CrossIterationStat> Stats { get; set; } = new();

    public static CrossIterationSummary Build(IReadOnlyList<MergeGroup> groups)
    {
        var summary = new CrossIterationSummary();
        foreach (var scope in groups
                     .GroupBy(g => $"{g.Target}|{g.Scenario}", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var all = scope.OrderBy(g => g.IterationNumber).ToList();
            var valid = all.Where(g => g.Valid).ToList();
            var observedIters = all.Select(g => g.IterationNumber).ToHashSet();
            // Expected iteration count declared by the hosts (max seen). Any expected iteration number
            // with NO artifacts at all is an entirely-missing iteration and must be surfaced as invalid.
            var expectedIterations = all.Count == 0 ? 0 : all.Max(g => Math.Max(g.IterationCount, g.IterationNumber));
            var missingIterationNumbers = Enumerable.Range(1, expectedIterations)
                .Where(n => !observedIters.Contains(n))
                .ToList();
            var invalidPresent = all.Where(g => !g.Valid).Select(g => g.IterationNumber);
            var stat = new CrossIterationStat
            {
                Target = all[0].Target,
                Scenario = all[0].Scenario,
                ExpectedIterations = expectedIterations,
                IterationsFound = all.Count,
                ValidIterations = valid.Count,
                InvalidIterationNumbers = invalidPresent.Concat(missingIterationNumbers).Distinct().OrderBy(x => x).ToList(),
                MissingIterationNumbers = missingIterationNumbers,
                MaxStartSkewSeconds = all.Count == 0 ? 0 : all.Max(g => g.StartSkewSeconds),
            };

            if (valid.Count > 0)
            {
                stat.MeanPeakConnPerSec = Math.Round(valid.Average(g => (double)g.PeakCombinedConnPerSec), 1);
                stat.MinPeakConnPerSec = valid.Min(g => g.PeakCombinedConnPerSec);
                stat.MaxPeakConnPerSec = valid.Max(g => g.PeakCombinedConnPerSec);
                stat.MeanPeakReadyPerSec = Math.Round(valid.Average(g => (double)g.PeakCombinedReadyPerSec), 1);
                stat.MinPeakReadyPerSec = valid.Min(g => g.PeakCombinedReadyPerSec);
                stat.MaxPeakReadyPerSec = valid.Max(g => g.PeakCombinedReadyPerSec);
                stat.MeanPeakActiveReady = Math.Round(valid.Average(g => (double)g.PeakCombinedActiveReady), 1);
                stat.MinPeakActiveReady = valid.Min(g => g.PeakCombinedActiveReady);
                stat.MaxPeakActiveReady = valid.Max(g => g.PeakCombinedActiveReady);
                stat.MeanPeakInFlight = Math.Round(valid.Average(g => (double)g.PeakCombinedInFlight), 1);
                stat.MinPeakInFlight = valid.Min(g => g.PeakCombinedInFlight);
                stat.MaxPeakInFlight = valid.Max(g => g.PeakCombinedInFlight);
                stat.MeanTrueE2eP99Ms = Math.Round(valid.Average(g => g.TrueE2eP99Ms), 2);
                stat.MinTrueE2eP99Ms = Math.Round(valid.Min(g => g.TrueE2eP99Ms), 2);
                stat.MaxTrueE2eP99Ms = Math.Round(valid.Max(g => g.TrueE2eP99Ms), 2);
                stat.MeanDrainDurationSeconds = Math.Round(valid.Average(g => g.DrainDurationSeconds), 2);
                stat.MinDrainDurationSeconds = Math.Round(valid.Min(g => g.DrainDurationSeconds), 2);
                stat.MaxDrainDurationSeconds = Math.Round(valid.Max(g => g.DrainDurationSeconds), 2);
                stat.MeanFailureRatePct = Math.Round(valid.Average(g => g.FailureRatePct), 3);
                stat.MinFailureRatePct = Math.Round(valid.Min(g => g.FailureRatePct), 3);
                stat.MaxFailureRatePct = Math.Round(valid.Max(g => g.FailureRatePct), 3);
                stat.AllIterationsReachedChurn = valid.All(g => g.ReachedChurnTarget);
                stat.AllIterationsReachedActiveReady = valid.All(g => g.ReachedConcurrentTarget);
            }

            summary.Stats.Add(stat);
        }

        return summary;
    }
}

/// <summary>Cross-iteration mean/min/max for one (target, scenario) scope.</summary>
public sealed class CrossIterationStat
{
    public string Target { get; set; } = string.Empty;

    public string Scenario { get; set; } = string.Empty;

    public int ExpectedIterations { get; set; }

    public int IterationsFound { get; set; }

    public int ValidIterations { get; set; }

    public List<int> InvalidIterationNumbers { get; set; } = new();

    /// <summary>Expected iteration numbers with NO artifacts at all (entirely-missing iterations).</summary>
    public List<int> MissingIterationNumbers { get; set; } = new();

    public long MaxStartSkewSeconds { get; set; }

    public double MeanPeakConnPerSec { get; set; }

    public long MinPeakConnPerSec { get; set; }

    public long MaxPeakConnPerSec { get; set; }

    public double MeanPeakReadyPerSec { get; set; }

    public long MinPeakReadyPerSec { get; set; }

    public long MaxPeakReadyPerSec { get; set; }

    public double MeanPeakActiveReady { get; set; }

    public long MinPeakActiveReady { get; set; }

    public long MaxPeakActiveReady { get; set; }

    public double MeanPeakInFlight { get; set; }

    public long MinPeakInFlight { get; set; }

    public long MaxPeakInFlight { get; set; }

    public double MeanTrueE2eP99Ms { get; set; }

    public double MinTrueE2eP99Ms { get; set; }

    public double MaxTrueE2eP99Ms { get; set; }

    public double MeanDrainDurationSeconds { get; set; }

    public double MinDrainDurationSeconds { get; set; }

    public double MaxDrainDurationSeconds { get; set; }

    public double MeanFailureRatePct { get; set; }

    public double MinFailureRatePct { get; set; }

    public double MaxFailureRatePct { get; set; }

    /// <summary>True when EVERY valid iteration met the churn (conn/s) target.</summary>
    public bool AllIterationsReachedChurn { get; set; }

    /// <summary>True when EVERY valid iteration met the active-ready concurrency target.</summary>
    public bool AllIterationsReachedActiveReady { get; set; }
}
