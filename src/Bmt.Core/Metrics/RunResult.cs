using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bmt.Core.Metrics;

/// <summary>
/// The machine-readable result of one <c>test</c> run against one target (test_instruction.md §8).
/// Serialized to <c>results/&lt;target&gt;-&lt;ts&gt;.json</c> and consumed by the <c>report</c> command.
/// Captures the full §7 metric set: per-op + lifecycle latency, connection counters and reuse
/// verification, the §7.4 error taxonomy, per-second throughput, and §7.3 client-host resource samples.
/// </summary>
public sealed class RunResult
{
    public string Target { get; set; } = string.Empty;

    public string Scenario { get; set; } = string.Empty;

    public string StartedUtc { get; set; } = string.Empty;

    public string FinishedUtc { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public string MaskedConnectionString { get; set; } = string.Empty;

    public int TaskSleepMs { get; set; }

    public long DatasetDocumentCount { get; set; }

    /// <summary>Whether a preflight ran and whether it permitted the run (gate result).</summary>
    public PreflightGateInfo Preflight { get; set; } = new();

    public TaskTotals Totals { get; set; } = new();

    /// <summary>Per-operation latency (find input / remove / insert / find output) — the §7.1  op breakdown.</summary>
    public Dictionary<string, LatencySummary> OperationLatencyMs { get; set; } = new();

    /// <summary>Full per-Task cycle latency: connect → 4 ops → disconnect (§7.1).</summary>
    public LatencySummary TaskCycleLatencyMs { get; set; } = LatencySummary.Empty();

    /// <summary>Driver connection-open (handshake/auth) latency (§7.1/§7.2).</summary>
    public LatencySummary ConnectionOpenMs { get; set; } = LatencySummary.Empty();

    /// <summary>MongoClient object-creation time (§7.1).</summary>
    public LatencySummary ClientCreateMs { get; set; } = LatencySummary.Empty();

    public ConnectionStats Connections { get; set; } = new();

    public ReuseVerification ReuseCheck { get; set; } = new();

    /// <summary>§7.4 error taxonomy counts (every failure classified into exactly one bucket).</summary>
    public Dictionary<string, long> ErrorsByType { get; set; } = new();

    /// <summary>Per-second throughput time-series (connections + per-op QPS), for the §8.1 graphs.</summary>
    public List<ThroughputPoint> Throughput { get; set; } = new();

    /// <summary>§7.3 client-host resource samples over time (ports / TIME_WAIT / handles / CPU / mem).</summary>
    public List<ResourceSample> ResourceSamples { get; set; } = new();

    /// <summary>Process CPU/memory peak summary (§7.1).</summary>
    public ProcessSummary Process { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RunResult FromJson(string json) =>
        JsonSerializer.Deserialize<RunResult>(json, ReadOptions)
        ?? throw new InvalidOperationException("RunResult JSON deserialized to null.");
}

public sealed class PreflightGateInfo
{
    public bool Ran { get; set; }

    public bool Passed { get; set; }

    public string Outcome { get; set; } = "not-run";

    /// <summary>True when calc_input's ReqId index is unique on this target (false on cosmos-ru).</summary>
    public bool InputIndexUnique { get; set; }

    /// <summary>True when this target's index uniqueness diverges from the canonical unique policy.</summary>
    public bool IndexUniquenessDiverges { get; set; }

    /// <summary>How distinct-ReqId is guaranteed even where the index is non-unique (recorded for the report).</summary>
    public string DistinctReqIdGuarantee { get; set; } = string.Empty;
}

public sealed class TaskTotals
{
    public long TotalTasks { get; set; }

    public long SuccessfulTasks { get; set; }

    public long FailedTasks { get; set; }

    public long TotalOps { get; set; }

    public long SuccessfulOps { get; set; }

    public long FailedOps { get; set; }
}

public sealed class ConnectionStats
{
    public long Created { get; set; }

    public long Ready { get; set; }

    public long Closed { get; set; }

    public long Failed { get; set; }

    public long CheckedOut { get; set; }

    /// <summary>connections created ÷ total Tasks — should be ≈ 1.0 in a correct no-reuse run.</summary>
    public double CreatedToTaskRatio { get; set; }

    /// <summary>connections closed ÷ total Tasks — should be ≈ 1.0 (every connection released).</summary>
    public double ClosedToTaskRatio { get; set; }
}

/// <summary>Result of the §7.2 client/session/cursor reuse verification.</summary>
public sealed class ReuseVerification
{
    /// <summary>True if no reuse was detected (created ≈ closed ≈ tasks, pool checkouts ≤ created).</summary>
    public bool NoReuseConfirmed { get; set; }

    public long SuspectedReuseEvents { get; set; }

    public string Detail { get; set; } = string.Empty;
}

/// <summary>One second of throughput (§7.1 per-op QPS + connection open/close rates).</summary>
public sealed class ThroughputPoint
{
    public int Second { get; set; }

    public long ConnectionsCreated { get; set; }

    public long ConnectionsClosed { get; set; }

    public long FindInputOps { get; set; }

    public long RemoveOps { get; set; }

    public long InsertOps { get; set; }

    public long FindOutputOps { get; set; }

    public long FailedOps { get; set; }

    public int InFlightTasks { get; set; }

    public long CombinedOps => FindInputOps + RemoveOps + InsertOps + FindOutputOps;
}

/// <summary>One §7.3 client-host resource sample.</summary>
public sealed class ResourceSample
{
    public int Second { get; set; }

    public int EphemeralPortsInUse { get; set; }

    public int TimeWaitSockets { get; set; }

    public int HandleCount { get; set; }

    public int ThreadCount { get; set; }

    public double CpuPercent { get; set; }

    public long WorkingSetBytes { get; set; }
}

public sealed class ProcessSummary
{
    public long PeakWorkingSetBytes { get; set; }

    public int PeakHandleCount { get; set; }

    public int PeakThreadCount { get; set; }

    public double MaxCpuPercent { get; set; }

    public int PeakEphemeralPortsInUse { get; set; }

    public int PeakTimeWaitSockets { get; set; }
}

/// <summary>The four ordered Task ops (§2.1). Keys used in <see cref="RunResult.OperationLatencyMs"/>.</summary>
public static class OpNames
{
    public const string FindInput = "find_input";
    public const string Remove = "remove";
    public const string Insert = "insert";
    public const string FindOutput = "find_output";

    public static readonly IReadOnlyList<string> Ordered = new[] { FindInput, Remove, Insert, FindOutput };
}
