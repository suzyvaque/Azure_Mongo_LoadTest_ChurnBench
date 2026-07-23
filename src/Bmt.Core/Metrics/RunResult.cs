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

    /// <summary>
    /// 1-based identifier of the load-generating host within a coordinated multi-host burst campaign
    /// (test_instruction.md §6.2 — reaching ≥11,000 concurrent / ≥1,200 conn/s requires several
    /// co-located generators). Default 1 for a single-host run.
    /// </summary>
    public int HostId { get; set; } = 1;

    /// <summary>Total number of load-generating hosts in the campaign (default 1 = single host).</summary>
    public int HostCount { get; set; } = 1;

    /// <summary>
    /// Optional shared tag identifying one coordinated multi-host campaign, so per-host artifacts from
    /// the same wall-clock window can be grouped by the <c>merge</c> command. Empty for solo runs.
    /// </summary>
    public string RunTag { get; set; } = string.Empty;

    /// <summary>
    /// Unix time (whole seconds) at which this iteration's timed clock started. Combined with each
    /// <see cref="ThroughputPoint.Second"/> it yields the absolute wall-clock second of every sample,
    /// letting the <c>merge</c> command sum conn/s and in-flight concurrency across hosts precisely.
    /// </summary>
    public long StartedUnixSeconds { get; set; }

    /// <summary>Workload mode label: "full-workload", "find-input", or "insert-output".</summary>
    public string WorkloadMode { get; set; } = "full-workload";

    /// <summary>1-based iteration number within the campaign (1 for single-iteration runs).</summary>
    public int IterationNumber { get; set; } = 1;

    /// <summary>Total number of iterations planned for this campaign.</summary>
    public int IterationCount { get; set; } = 1;

    public string StartedUtc { get; set; } = string.Empty;

    public string FinishedUtc { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public string MaskedConnectionString { get; set; } = string.Empty;

    public int TaskSleepMs { get; set; }

    public long DatasetDocumentCount { get; set; }

    /// <summary>Whether a preflight ran and whether it permitted the run (gate result).</summary>
    public PreflightGateInfo Preflight { get; set; } = new();

    public TaskTotals Totals { get; set; } = new();

    /// <summary>
    /// §2 open-loop generator-fidelity + latency decomposition: scheduled/started counts and rates,
    /// scheduler-queue / execution / true offered-to-finished latency (authoritative over ALL Tasks
    /// offered during arrival, plus a secondary arrival-completed-only view).
    /// </summary>
    public OpenLoopStats OpenLoop { get; set; } = new();

    /// <summary>§2 explicit arrival-vs-drain iteration model (window bounds, drain duration, backlog).</summary>
    public ArrivalDrainStats Arrival { get; set; } = new();

    /// <summary>Per-operation latency (find input / remove / insert / find output) — the §7.1  op breakdown.</summary>
    public Dictionary<string, LatencySummary> OperationLatencyMs { get; set; } = new();

    /// <summary>Full per-Task cycle latency: connect → 4 ops → disconnect (§7.1).</summary>
    public LatencySummary TaskCycleLatencyMs { get; set; } = LatencySummary.Empty();

    /// <summary>Driver connection-open (handshake/auth) latency (§7.1/§7.2).</summary>
    public LatencySummary ConnectionOpenMs { get; set; } = LatencySummary.Empty();

    /// <summary>
    /// Handshake wire-negotiation latency: the driver's <c>hello</c>/<c>isMaster</c> command issued on
    /// each brand-new connection during establishment (subset of <see cref="ConnectionOpenMs"/>).
    /// </summary>
    public LatencySummary HandshakeHelloMs { get; set; } = LatencySummary.Empty();

    /// <summary>
    /// SCRAM authentication latency: the <c>saslStart</c>/<c>saslContinue</c> commands issued on each
    /// brand-new connection (subset of <see cref="ConnectionOpenMs"/>). Captured uniformly for every
    /// target — including DocumentDB, which authenticates with SCRAM-SHA-256 — so the auth cost of the
    /// cold-connection storm is directly comparable across backends.
    /// </summary>
    public LatencySummary HandshakeAuthMs { get; set; } = LatencySummary.Empty();

    /// <summary>MongoClient object-creation time (§7.1).</summary>
    public LatencySummary ClientCreateMs { get; set; } = LatencySummary.Empty();

    public ConnectionStats Connections { get; set; } = new();

    /// <summary>
    /// §3 connection-lifecycle model (driver-event-sourced): per-state counters + peak gauges, the two
    /// cold-connection latencies (demand→ready and driver-open), and a lifecycle reconciliation. This is
    /// the AUTHORITATIVE connection evidence — Task counts and host TCP sockets are not interchangeable.
    /// </summary>
    public ConnectionLifecycleStats Lifecycle { get; set; } = new();

    public ReuseVerification ReuseCheck { get; set; } = new();

    /// <summary>§7.4 error taxonomy counts (every failure classified into exactly one bucket).</summary>
    public Dictionary<string, long> ErrorsByType { get; set; } = new();

    /// <summary>Per-second throughput time-series (connections + per-op QPS), for the §8.1 graphs.</summary>
    public List<ThroughputPoint> Throughput { get; set; } = new();

    /// <summary>§7.3 client-host resource samples over time (ports / TIME_WAIT / handles / CPU / mem).</summary>
    public List<ResourceSample> ResourceSamples { get; set; } = new();

    /// <summary>
    /// §4 target-specific TCP telemetry: per-second (sub-second-peak) counts of sockets to the RESOLVED
    /// database endpoints only, by TCP state, plus host-wide totals and ephemeral-port pressure. Driver
    /// events remain the authoritative connection source; this explains WHERE non-ready connections wait.
    /// </summary>
    public List<TargetTcpSample> TargetTcpSamples { get; set; } = new();

    /// <summary>§4 resolved target endpoint set + ephemeral range + telemetry-integrity metadata.</summary>
    public TargetTcpInfo TargetTcp { get; set; } = new();

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

    /// <summary>§2: Tasks OFFERED to the runtime during the arrival window (each = one connection demand).</summary>
    public long TasksScheduled { get; set; }

    /// <summary>§2: Tasks that actually began executing (dequeued by the runtime).</summary>
    public long TasksStarted { get; set; }

    /// <summary>§2: peak scheduled-but-not-started backlog (thread-pool dispatch pressure).</summary>
    public long PeakScheduledNotStartedBacklog { get; set; }
}

/// <summary>
/// §2 open-loop metrics. Rates use the AUTHORITATIVE 300-second arrival window as the denominator (not
/// total process duration), so offered load is not understated by drain time. Latency is decomposed
/// into scheduler-queue delay (runtime dispatch), execution, and true offered-to-finished (the
/// authoritative open-loop end-to-end). Every Task offered during arrival is included in the
/// authoritative digests even if it completes during drain; the "-Arrival" digests are the secondary
/// view over only Tasks that also completed before arrival stopped.
/// </summary>
public sealed class OpenLoopStats
{
    /// <summary>Length (s) of the arrival window used as the denominator for the rates below.</summary>
    public double ArrivalWindowSeconds { get; set; }

    /// <summary>Offered/scheduled Tasks per second = TasksScheduled / ArrivalWindowSeconds.</summary>
    public double ScheduledTasksPerSec { get; set; }

    /// <summary>Started Tasks per second = TasksStarted / ArrivalWindowSeconds.</summary>
    public double StartedTasksPerSec { get; set; }

    /// <summary>Number of Tasks that COMPLETED before the arrival window closed (excludes the slow tail).</summary>
    public long TasksCompletedDuringArrival { get; set; }

    // ---- Authoritative: ALL Tasks offered during arrival (including drain completions). ----
    public LatencySummary SchedulerQueueLatencyMs { get; set; } = LatencySummary.Empty();

    public LatencySummary TaskExecutionLatencyMs { get; set; } = LatencySummary.Empty();

    /// <summary>True open-loop end-to-end latency (ScheduledUtc → TaskFinishedUtc). The headline figure.</summary>
    public LatencySummary OfferedToFinishedLatencyMs { get; set; } = LatencySummary.Empty();

    // ---- Secondary: only Tasks that COMPLETED during the arrival window. ----
    public LatencySummary SchedulerQueueLatencyArrivalMs { get; set; } = LatencySummary.Empty();

    public LatencySummary TaskExecutionLatencyArrivalMs { get; set; } = LatencySummary.Empty();

    public LatencySummary OfferedToFinishedLatencyArrivalMs { get; set; } = LatencySummary.Empty();
}

/// <summary>
/// §2 explicit iteration model: arrival window bounds, drain bounds, and the backlog carried into drain.
/// Timestamps are ISO-8601 UTC. The 300-second arrival window (<see cref="OpenLoopStats.ArrivalWindowSeconds"/>)
/// is the authoritative denominator; the measured arrival span is recorded for transparency.
/// </summary>
public sealed class ArrivalDrainStats
{
    public string ArrivalStartedUtc { get; set; } = string.Empty;

    public string ArrivalStoppedUtc { get; set; } = string.Empty;

    /// <summary>Authoritative arrival window (s) — the intended/configured duration used for all rates.</summary>
    public double ArrivalDurationSeconds { get; set; }

    /// <summary>Measured wall-clock span from arrival start to arrival stop (diagnostic).</summary>
    public double MeasuredArrivalDurationSeconds { get; set; }

    public string DrainStartedUtc { get; set; } = string.Empty;

    public string DrainFinishedUtc { get; set; } = string.Empty;

    public double DrainDurationSeconds { get; set; }

    public double TotalIterationDurationSeconds { get; set; }

    /// <summary>Tasks scheduled but not yet finished at the instant arrival stopped (= drain-start backlog).</summary>
    public long TasksOutstandingAtArrivalStop { get; set; }

    /// <summary>In-flight Tasks (started, not finished) at the instant arrival stopped.</summary>
    public long InFlightAtArrivalStop { get; set; }

    /// <summary>Concurrent driver-ready connections at arrival stop (populated by the lifecycle model).</summary>
    public long ConnectionsReadyAtArrivalStop { get; set; }

    /// <summary>Maximum outstanding-Task backlog observed during drain.</summary>
    public long MaximumDrainBacklog { get; set; }
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

/// <summary>
/// §3 connection-lifecycle model. Driver connection-monitoring events are the authoritative source; do
/// NOT infer connection acceptance from scheduled Tasks/s, nor concurrent established connections from
/// in-flight Task count. Counter semantics are documented on <see cref="Bmt.Core.Connections.ConnectionEventCounters"/>.
/// </summary>
public sealed class ConnectionLifecycleStats
{
    public long TasksScheduled { get; set; }

    public long TasksStarted { get; set; }

    /// <summary>Tasks that reached connection demand (about to acquire a connection) — the reconciliation floor.</summary>
    public long TasksReachedDemand { get; set; }

    public long ConnectionsCreated { get; set; }

    public long ConnectionsReady { get; set; }

    public long ConnectionsFailed { get; set; }

    public long ConnectionsClosed { get; set; }

    /// <summary>Peak concurrent operations in driver server-selection (the WaitingForServer state).</summary>
    public int PeakWaitingForServer { get; set; }

    /// <summary>Peak concurrent connections in the Connecting state (created, not yet ready).</summary>
    public int PeakActiveConnecting { get; set; }

    /// <summary>Peak concurrent connections in the Ready state — the §3 concurrent-ready evidence (≥ 11,000).</summary>
    public int PeakActiveReady { get; set; }

    /// <summary>Peak concurrent connections in the Closing state (closing, not yet closed).</summary>
    public int PeakActiveClosing { get; set; }

    /// <summary>Connections still Connecting at the end of the run (should be ~0 after drain).</summary>
    public int ResidualActiveConnecting { get; set; }

    /// <summary>Connections still Ready (not closed) at the end of the run (should be ~0 after drain).</summary>
    public int ResidualActiveReady { get; set; }

    /// <summary>Connections still Closing at the end of the run (should be ~0 after drain).</summary>
    public int ResidualActiveClosing { get; set; }

    /// <summary>Demand → driver-ready latency: user-observed cold-connection cost (server selection + DNS/SRV + TCP + TLS + hello + auth).</summary>
    public LatencySummary DemandToReadyLatencyMs { get; set; } = LatencySummary.Empty();

    /// <summary>Driver-created → ready latency: isolates the physical connection-open lifecycle.</summary>
    public LatencySummary DriverOpenLatencyMs { get; set; } = LatencySummary.Empty();

    public long CreatedMinusClosed { get; set; }

    /// <summary>ConnectionsCreated − TasksReachedDemand. ~0 ideal; negative = Tasks that failed before creating a connection.</summary>
    public long CreatedMinusDemand { get; set; }

    /// <summary>True when created ≈ closed after drain (no leak / no reuse). A mismatch is reported explicitly.</summary>
    public bool LifecycleReconciled { get; set; }

    public string ReconciliationDetail { get; set; } = string.Empty;
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

    /// <summary>§2: Tasks OFFERED to the runtime this second (open-loop arrival rate).</summary>
    public long ScheduledTasks { get; set; }

    /// <summary>§2: Tasks that began executing this second (realized start rate).</summary>
    public long StartedTasks { get; set; }

    public long ConnectionsCreated { get; set; }

    /// <summary>§3: connections that became driver-ready this second.</summary>
    public long ConnectionsReady { get; set; }

    /// <summary>§3: connections that failed to open this second.</summary>
    public long ConnectionsFailed { get; set; }

    public long ConnectionsClosed { get; set; }

    /// <summary>§3: peak concurrent Connecting connections observed this second.</summary>
    public int ActiveConnecting { get; set; }

    /// <summary>§3: peak concurrent Ready connections observed this second.</summary>
    public int ActiveReady { get; set; }

    /// <summary>§3: peak concurrent WaitingForServer operations observed this second.</summary>
    public int WaitingForServer { get; set; }

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

/// <summary>
/// One second of §4 target-specific TCP telemetry. Values are the SUB-SECOND PEAK across the raw
/// samples taken that second (raw sampling runs every 250–500 ms). Only sockets whose remote endpoint
/// matches the resolved target IP/port set are counted as Target*; Host* are VM-wide totals for
/// general-pressure context (never used as database-specific evidence).
/// </summary>
public sealed class TargetTcpSample
{
    public int Second { get; set; }

    public int TargetSynSent { get; set; }

    public int TargetEstablished { get; set; }

    public int TargetTimeWait { get; set; }

    public int TargetCloseWait { get; set; }

    public int TargetFinWait1 { get; set; }

    public int TargetFinWait2 { get; set; }

    public int TargetTotalSockets { get; set; }

    public int TargetDistinctLocalPorts { get; set; }

    public int HostTotalTcpSockets { get; set; }

    public int HostTotalTimeWait { get; set; }

    /// <summary>Distinct host local ports currently in use within the ephemeral range.</summary>
    public int EphemeralPortsInUse { get; set; }

    /// <summary>Estimated ephemeral-port utilization (% of the dynamic range in use).</summary>
    public double EphemeralUtilizationPct { get; set; }
}

/// <summary>§4 resolved endpoint set + ephemeral range + telemetry-integrity metadata and peaks.</summary>
public sealed class TargetTcpInfo
{
    public string ResolvedAtUtc { get; set; } = string.Empty;

    /// <summary>The resolved destination IP:port set the sampler filtered against this iteration.</summary>
    public List<string> Endpoints { get; set; } = new();

    public int EndpointCount { get; set; }

    public int EphemeralRangeStart { get; set; }

    public int EphemeralRangeEnd { get; set; }

    /// <summary>Raw TCP samples that were dropped (enumeration error/timeout) — telemetry-integrity signal.</summary>
    public int DroppedSamples { get; set; }

    public int RawSampleIntervalMs { get; set; }

    /// <summary>Human-readable note on expected telemetry overhead (documented, not measured per-run).</summary>
    public string OverheadNote { get; set; } = string.Empty;

    // ---- Sub-second peaks over the whole iteration ----
    public int PeakTargetSynSent { get; set; }

    public int PeakTargetEstablished { get; set; }

    public int PeakTargetTimeWait { get; set; }

    public int PeakTargetCloseWait { get; set; }

    public int PeakTargetFinWait1 { get; set; }

    public int PeakTargetFinWait2 { get; set; }

    public int PeakTargetTotalSockets { get; set; }

    public int PeakTargetDistinctLocalPorts { get; set; }

    public int PeakHostTotalTcpSockets { get; set; }

    public int PeakHostTotalTimeWait { get; set; }

    public int PeakEphemeralPortsInUse { get; set; }

    public double PeakEphemeralUtilizationPct { get; set; }
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
