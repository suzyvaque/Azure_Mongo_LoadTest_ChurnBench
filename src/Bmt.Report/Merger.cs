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
                     .GroupBy(r => $"{r.Target}|{r.Scenario}", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mg = BuildGroup(group.ToList());
            mg.ReachedChurnTarget = mg.PeakCombinedConnPerSec >= churnTarget;
            mg.ReachedConcurrentTarget = mg.PeakCombinedInFlight >= concurrentTarget;
            report.Groups.Add(mg);
        }

        return report;
    }

    private static MergeGroup BuildGroup(IReadOnlyList<RunResult> hostRuns)
    {
        // Absolute wall-clock second -> summed connection-open rate + summed in-flight concurrency.
        var connBySecond = new Dictionary<long, long>();
        var inFlightBySecond = new Dictionary<long, long>();
        var opsBySecond = new Dictionary<long, long>();
        var failedBySecond = new Dictionary<long, long>();

        foreach (var run in hostRuns)
        {
            foreach (var p in run.Throughput)
            {
                var sec = run.StartedUnixSeconds + p.Second;
                connBySecond[sec] = connBySecond.GetValueOrDefault(sec) + p.ConnectionsCreated;
                inFlightBySecond[sec] = inFlightBySecond.GetValueOrDefault(sec) + p.InFlightTasks;
                opsBySecond[sec] = opsBySecond.GetValueOrDefault(sec) + p.CombinedOps;
                failedBySecond[sec] = failedBySecond.GetValueOrDefault(sec) + p.FailedOps;
            }
        }

        var first = hostRuns[0];
        var hostIds = hostRuns.Select(r => r.HostId).Distinct().OrderBy(x => x).ToList();
        var declaredHostCount = hostRuns.Max(r => r.HostCount);

        var peakConn = connBySecond.Count == 0 ? 0 : connBySecond.Values.Max();
        var peakConc = inFlightBySecond.Count == 0 ? 0 : inFlightBySecond.Values.Max();

        var group = new MergeGroup
        {
            Target = first.Target,
            Scenario = first.Scenario,
            WorkloadMode = first.WorkloadMode,
            RunTag = first.RunTag,
            HostsFound = hostIds.Count,
            DeclaredHostCount = declaredHostCount,
            HostIds = hostIds,
            PeakCombinedConnPerSec = peakConn,
            PeakCombinedInFlight = peakConc,
            TotalConnectionsCreated = connBySecond.Values.Sum(),
            CombinedWindowSeconds = connBySecond.Count,
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

    public int HostsFound { get; set; }

    public int DeclaredHostCount { get; set; }

    public List<int> HostIds { get; set; } = new();

    /// <summary>Peak of the per-second SUM of per-host connections opened — the combined conn/s spike.</summary>
    public long PeakCombinedConnPerSec { get; set; }

    /// <summary>
    /// Peak of the per-second SUM of per-host in-flight Tasks — an upper-bound estimate of combined
    /// concurrency (per-host per-second maxima may occur at slightly different sub-second offsets).
    /// Corroborate with server-side connection counts for the authoritative peak.
    /// </summary>
    public long PeakCombinedInFlight { get; set; }

    public long TotalConnectionsCreated { get; set; }

    public int CombinedWindowSeconds { get; set; }

    /// <summary>True when the combined conn/s peak met the campaign churn target (default ≥ 1,200).</summary>
    public bool ReachedChurnTarget { get; set; }

    /// <summary>True when the combined concurrency peak met the campaign concurrent target (default ≥ 11,000).</summary>
    public bool ReachedConcurrentTarget { get; set; }

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
