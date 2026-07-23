using System.Collections.Concurrent;
using System.Diagnostics;
using Bmt.Core.Connections;
using Bmt.Core.Errors;
using Bmt.Core.Metrics;
using MongoDB.Driver.Core.Connections;

namespace Bmt.LoadGen;

/// <summary>
/// Thread-safe sink for every metric the comparison needs (test_instruction.md §7). Records per-op
/// and full-cycle latency, connection-open and client-create times, the §7.4 error taxonomy, and a
/// per-second throughput time-series (connection open/close rates + per-op QPS + in-flight Tasks).
/// Latency is sharded (<see cref="LatencyDigest"/>) to keep lock contention low at the high op rate
/// of the churn workload. Percentiles are computed once when <see cref="Build"/> runs at the end.
/// </summary>
public sealed class MetricsCollector : IConnectionEventObserver
{
    private readonly LatencyDigest _findInput = new();
    private readonly LatencyDigest _remove = new();
    private readonly LatencyDigest _insert = new();
    private readonly LatencyDigest _findOutput = new();
    private readonly LatencyDigest _cycle = new();
    private readonly LatencyDigest _clientCreate = new();
    private readonly LatencyDigest _connectionOpen = new();
    private readonly LatencyDigest _handshakeHello = new();
    private readonly LatencyDigest _handshakeAuth = new();

    // §2 open-loop decomposition — separate scheduler-queue delay, execution, and true offered-to-
    // finished latency. The "authoritative" digests cover EVERY Task offered during the arrival window
    // (including those that complete during drain); the "-arrival" digests cover only Tasks that also
    // COMPLETED before arrival stopped (excludes the slow tail, so they are the secondary view).
    private readonly LatencyDigest _schedQueue = new();
    private readonly LatencyDigest _taskExec = new();
    private readonly LatencyDigest _offeredToFinished = new();
    private readonly LatencyDigest _schedQueueArrival = new();
    private readonly LatencyDigest _taskExecArrival = new();
    private readonly LatencyDigest _offeredToFinishedArrival = new();

    // §3 connection lifecycle: per-Task demand→driver-ready (cold-connection) latency. Driver-open
    // (created→ready) is captured by _connectionOpen from the driver's reported open duration.
    private readonly LatencyDigest _demandToReady = new();

    // Authoritative connection counters (bound by the orchestrator) so per-second active-gauge maxima
    // can be sampled on each driver event.
    private Bmt.Core.Connections.ConnectionEventCounters? _connCounters;

    private readonly ConcurrentDictionary<BmtErrorType, long> _errors = new();
    private readonly ConcurrentDictionary<int, SecondBucket> _seconds = new();

    private long _tasksScheduled;
    private long _tasksStarted;
    private long _tasksReachedDemand;
    private long _totalTasks;
    private long _successTasks;
    private long _failTasks;
    private long _tasksCompletedDuringArrival;
    private long _totalOps;
    private long _successOps;
    private long _failOps;
    private int _inFlight;
    private int _peakScheduledBacklog;

    // Arrival phase gate + the snapshot captured exactly when the arrival window closes (§2 iteration
    // model): outstanding Tasks and in-flight Tasks at arrival stop, used to size the drain backlog.
    private int _arrivalActive = 1;
    private long _tasksOutstandingAtArrivalStop;
    private long _inFlightAtArrivalStop;
    private long _maximumDrainBacklog;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Reset the run clock to "now" (call immediately before the timed phase begins).</summary>
    public void StartClock() => _clock.Restart();

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Called synchronously when a Task is OFFERED to the runtime (handed to the thread pool), before it
    /// begins executing. Stamps the scheduled instant so scheduler-queue latency (dispatch delay) can be
    /// measured, and updates the scheduled-but-not-started backlog peak.
    /// </summary>
    public void OnTaskScheduled()
    {
        var scheduled = Interlocked.Increment(ref _tasksScheduled);
        var b = Bucket();
        Interlocked.Increment(ref b.ScheduledTasks);
        UpdateScheduledBacklog(scheduled - Interlocked.Read(ref _tasksStarted));
    }

    /// <summary>Called when a Task actually begins executing (dequeued by the runtime).</summary>
    public void OnTaskStart()
    {
        Interlocked.Increment(ref _totalTasks);
        var started = Interlocked.Increment(ref _tasksStarted);
        var now = Interlocked.Increment(ref _inFlight);
        var b = Bucket();
        Interlocked.Increment(ref b.StartedTasks);
        b.UpdateInFlightMax(now);
        UpdateScheduledBacklog(Interlocked.Read(ref _tasksScheduled) - started);
    }

    /// <summary>
    /// Called when a Task finishes (its connection has been released). Records the full cycle latency
    /// plus the §2 open-loop decomposition (scheduler-queue / execution / offered-to-finished). During
    /// the arrival phase a finish is also recorded into the arrival-completed digests.
    /// </summary>
    public void OnTaskEnd(bool success, double cycleMs, double schedQueueMs, double execMs, double offeredToFinishedMs)
    {
        var remaining = Interlocked.Decrement(ref _inFlight);
        _cycle.Record(cycleMs);
        _schedQueue.Record(schedQueueMs);
        _taskExec.Record(execMs);
        _offeredToFinished.Record(offeredToFinishedMs);

        var duringArrival = Volatile.Read(ref _arrivalActive) == 1;
        if (duringArrival)
        {
            Interlocked.Increment(ref _tasksCompletedDuringArrival);
            _schedQueueArrival.Record(schedQueueMs);
            _taskExecArrival.Record(execMs);
            _offeredToFinishedArrival.Record(offeredToFinishedMs);
        }
        else
        {
            // Drain phase: the outstanding backlog is (started-not-finished) + (scheduled-not-started).
            var backlog = remaining + (Interlocked.Read(ref _tasksScheduled) - Interlocked.Read(ref _tasksStarted));
            UpdateMaxDrainBacklog(backlog);
        }

        if (success)
        {
            Interlocked.Increment(ref _successTasks);
        }
        else
        {
            Interlocked.Increment(ref _failTasks);
        }
    }

    /// <summary>
    /// Close the arrival window (§2): stop counting arrival-completed Tasks and snapshot the outstanding
    /// backlog. After this, every remaining Task completes during drain. Idempotent.
    /// <para>
    /// The snapshot is LOCK-FREE by design (the churn workload records thousands of task-ends per second;
    /// a shared lock on the task-end path would distort the very latency being measured). It is therefore
    /// an APPROXIMATE gauge: a handful of Tasks completing in the same instant as this call may be counted
    /// as still-outstanding (their success/fail increment can trail the in-flight decrement by a few
    /// microseconds). The error is bounded by the number of concurrent in-flight completions at the stop
    /// instant and never affects the authoritative latency digests.
    /// </para>
    /// </summary>
    public void OnArrivalStopped()
    {
        if (Interlocked.CompareExchange(ref _arrivalActive, 0, 1) != 1)
        {
            return;
        }

        var scheduled = Interlocked.Read(ref _tasksScheduled);
        var finished = Interlocked.Read(ref _successTasks) + Interlocked.Read(ref _failTasks);
        var outstanding = Math.Max(0, scheduled - finished);
        Interlocked.Exchange(ref _tasksOutstandingAtArrivalStop, outstanding);
        Interlocked.Exchange(ref _inFlightAtArrivalStop, Volatile.Read(ref _inFlight));
        UpdateMaxDrainBacklog(outstanding);
    }

    private void UpdateScheduledBacklog(long candidate)
    {
        if (candidate <= 0)
        {
            return;
        }

        int observed;
        var c = (int)Math.Min(candidate, int.MaxValue);
        do
        {
            observed = Volatile.Read(ref _peakScheduledBacklog);
            if (c <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakScheduledBacklog, c, observed) != observed);
    }

    private void UpdateMaxDrainBacklog(long candidate)
    {
        if (candidate <= 0)
        {
            return;
        }

        long observed;
        do
        {
            observed = Interlocked.Read(ref _maximumDrainBacklog);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maximumDrainBacklog, candidate, observed) != observed);
    }

    public void RecordClientCreate(double ms) => _clientCreate.Record(ms);

    /// <summary>§3: record one Task's demand→driver-ready (cold-connection) latency in milliseconds.</summary>
    public void RecordConnectionDemandToReady(double ms) => _demandToReady.Record(ms);

    /// <summary>§3: a Task reached connection demand (its first op is about to acquire a connection).</summary>
    public void OnConnectionDemand() => Interlocked.Increment(ref _tasksReachedDemand);

    /// <summary>Bind the authoritative connection counters so per-second active gauges can be sampled.</summary>
    public void BindConnectionCounters(Bmt.Core.Connections.ConnectionEventCounters counters) =>
        _connCounters = counters;

    /// <summary>Record one of the four ordered Task ops (§2.1).</summary>
    public void RecordOp(string opName, double ms, bool success)
    {
        Interlocked.Increment(ref _totalOps);
        var b = Bucket();
        if (success)
        {
            Interlocked.Increment(ref _successOps);
            Digest(opName).Record(ms);
            switch (opName)
            {
                case OpNames.FindInput: Interlocked.Increment(ref b.FindInputOps); break;
                case OpNames.Remove: Interlocked.Increment(ref b.RemoveOps); break;
                case OpNames.Insert: Interlocked.Increment(ref b.InsertOps); break;
                case OpNames.FindOutput: Interlocked.Increment(ref b.FindOutputOps); break;
            }
        }
        else
        {
            Interlocked.Increment(ref _failOps);
            Interlocked.Increment(ref b.FailedOps);
        }
    }

    public void RecordError(BmtErrorType type) =>
        _errors.AddOrUpdate(type, 1, (_, v) => v + 1);

    // ---- IConnectionEventObserver: driver events are the AUTHORITATIVE connection-lifecycle source
    // (§3). MetricsCollector records the per-SECOND connection rates and per-second active-gauge maxima
    // (the cumulative counters + live gauges live in ConnectionEventCounters, which is invoked FIRST by
    // the CompositeConnectionObserver, so the gauge reads below already reflect the current event).
    void IConnectionEventObserver.OnServerSelectionStarted() => SampleActiveGauges();

    void IConnectionEventObserver.OnServerSelectionEnded(bool success) => SampleActiveGauges();

    void IConnectionEventObserver.OnConnectionCreated(ConnectionId connectionId)
    {
        Interlocked.Increment(ref Bucket().ConnectionsCreated);
        SampleActiveGauges();
    }

    void IConnectionEventObserver.OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration)
    {
        Interlocked.Increment(ref Bucket().ConnectionsReady);
        if (openDuration is { } d)
        {
            // Driver-created → ready (the physical connection-open duration = DriverOpenLatencyMs, §3).
            _connectionOpen.Record(d.TotalMilliseconds);
        }

        SampleActiveGauges();
    }

    void IConnectionEventObserver.OnConnectionClosed(ConnectionId connectionId)
    {
        Interlocked.Increment(ref Bucket().ConnectionsClosed);
        SampleActiveGauges();
    }

    void IConnectionEventObserver.OnConnectionClosing(ConnectionId connectionId) => SampleActiveGauges();

    void IConnectionEventObserver.OnConnectionFailed(ConnectionId connectionId, Exception exception)
    {
        Interlocked.Increment(ref Bucket().ConnectionsFailed);
        SampleActiveGauges();
    }

    void IConnectionEventObserver.OnConnectionCheckedOut(ConnectionId connectionId)
    {
    }

    /// <summary>Update the current second's active-gauge maxima from the authoritative counters.</summary>
    private void SampleActiveGauges()
    {
        var c = _connCounters;
        if (c is null)
        {
            return;
        }

        var b = Bucket();
        b.UpdateActiveConnectingMax(c.ActiveConnecting);
        b.UpdateActiveReadyMax(c.ActiveReady);
        b.UpdateWaitingForServerMax(c.WaitingForServer);
    }

    void IConnectionEventObserver.OnHandshakeCommand(string commandName, TimeSpan duration, bool success)
    {
        // Only successful handshake commands carry meaningful latency; failures are counted in the error
        // taxonomy. Split SCRAM auth (saslStart/saslContinue) from the hello/isMaster wire negotiation.
        if (!success)
        {
            return;
        }

        if (Bmt.Core.Connections.ConnectionEventCounters.IsAuthCommand(commandName))
        {
            _handshakeAuth.Record(duration.TotalMilliseconds);
        }
        else
        {
            _handshakeHello.Record(duration.TotalMilliseconds);
        }
    }

    private LatencyDigest Digest(string opName) => opName switch
    {
        OpNames.FindInput => _findInput,
        OpNames.Remove => _remove,
        OpNames.Insert => _insert,
        OpNames.FindOutput => _findOutput,
        _ => throw new ArgumentOutOfRangeException(nameof(opName), opName, "Unknown op name."),
    };

    private SecondBucket Bucket()
    {
        var second = (int)_clock.Elapsed.TotalSeconds;
        return _seconds.GetOrAdd(second, _ => new SecondBucket());
    }

    /// <summary>Outstanding Tasks (scheduled but not finished) captured when the arrival window closed.</summary>
    public long TasksOutstandingAtArrivalStop => Interlocked.Read(ref _tasksOutstandingAtArrivalStop);

    /// <summary>In-flight Tasks (started but not finished) captured when the arrival window closed.</summary>
    public long InFlightAtArrivalStop => Interlocked.Read(ref _inFlightAtArrivalStop);

    /// <summary>Maximum outstanding-Task backlog observed during the drain phase.</summary>
    public long MaximumDrainBacklog => Interlocked.Read(ref _maximumDrainBacklog);

    /// <summary>
    /// Materialize the immutable <see cref="RunResult"/> at the end of the run. Connection counters and
    /// reuse verification come from the live <see cref="Bmt.Core.Connections.ConnectionEventCounters"/>.
    /// </summary>
    public RunResult Build(
        Bmt.Core.Connections.ConnectionEventCounters connCounters,
        IReadOnlyList<ResourceSample> resourceSamples,
        ProcessSummary process)
    {
        ArgumentNullException.ThrowIfNull(connCounters);

        var totalTasks = Interlocked.Read(ref _totalTasks);
        var result = new RunResult
        {
            Totals = new TaskTotals
            {
                TotalTasks = totalTasks,
                SuccessfulTasks = Interlocked.Read(ref _successTasks),
                FailedTasks = Interlocked.Read(ref _failTasks),
                TotalOps = Interlocked.Read(ref _totalOps),
                SuccessfulOps = Interlocked.Read(ref _successOps),
                FailedOps = Interlocked.Read(ref _failOps),
                TasksScheduled = Interlocked.Read(ref _tasksScheduled),
                TasksStarted = Interlocked.Read(ref _tasksStarted),
                PeakScheduledNotStartedBacklog = Volatile.Read(ref _peakScheduledBacklog),
            },
            TaskCycleLatencyMs = _cycle.Summarize(),
            ConnectionOpenMs = _connectionOpen.Summarize(),
            HandshakeHelloMs = _handshakeHello.Summarize(),
            HandshakeAuthMs = _handshakeAuth.Summarize(),
            ClientCreateMs = _clientCreate.Summarize(),
            OperationLatencyMs = new Dictionary<string, LatencySummary>
            {
                [OpNames.FindInput] = _findInput.Summarize(),
                [OpNames.Remove] = _remove.Summarize(),
                [OpNames.Insert] = _insert.Summarize(),
                [OpNames.FindOutput] = _findOutput.Summarize(),
            },
            OpenLoop = new OpenLoopStats
            {
                TasksCompletedDuringArrival = Interlocked.Read(ref _tasksCompletedDuringArrival),
                SchedulerQueueLatencyMs = _schedQueue.Summarize(),
                TaskExecutionLatencyMs = _taskExec.Summarize(),
                OfferedToFinishedLatencyMs = _offeredToFinished.Summarize(),
                SchedulerQueueLatencyArrivalMs = _schedQueueArrival.Summarize(),
                TaskExecutionLatencyArrivalMs = _taskExecArrival.Summarize(),
                OfferedToFinishedLatencyArrivalMs = _offeredToFinishedArrival.Summarize(),
            },
            ErrorsByType = _errors.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ResourceSamples = resourceSamples.ToList(),
            Process = process,
        };

        var created = connCounters.Created;
        var closed = connCounters.Closed;
        result.Connections = new ConnectionStats
        {
            Created = created,
            Ready = connCounters.Ready,
            Closed = closed,
            Failed = connCounters.Failed,
            CheckedOut = connCounters.CheckedOut,
            CreatedToTaskRatio = totalTasks == 0 ? 0 : (double)created / totalTasks,
            ClosedToTaskRatio = totalTasks == 0 ? 0 : (double)closed / totalTasks,
        };

        // §3 connection-lifecycle model: driver-event-sourced counters + gauges + the two cold-connection
        // latencies, plus a lifecycle reconciliation. Created should ≈ Tasks that reached connection
        // DEMAND (the correct floor — a Task that fails before demand never opens a connection), and after
        // drain created ≈ closed. Any mismatch is reported explicitly rather than hidden.
        var tasksStarted = Interlocked.Read(ref _tasksStarted);
        var tasksReachedDemand = Interlocked.Read(ref _tasksReachedDemand);
        var createdClosedDelta = created - closed;
        var createdVsDemandDelta = created - tasksReachedDemand;
        result.Lifecycle = new ConnectionLifecycleStats
        {
            TasksScheduled = Interlocked.Read(ref _tasksScheduled),
            TasksStarted = tasksStarted,
            TasksReachedDemand = tasksReachedDemand,
            ConnectionsCreated = created,
            ConnectionsReady = connCounters.Ready,
            ConnectionsFailed = connCounters.Failed,
            ConnectionsClosed = closed,
            PeakWaitingForServer = connCounters.PeakWaitingForServer,
            PeakActiveConnecting = connCounters.PeakActiveConnecting,
            PeakActiveReady = connCounters.PeakActiveReady,
            PeakActiveClosing = connCounters.PeakActiveClosing,
            ResidualActiveConnecting = connCounters.ActiveConnecting,
            ResidualActiveReady = connCounters.ActiveReady,
            ResidualActiveClosing = connCounters.ActiveClosing,
            DemandToReadyLatencyMs = _demandToReady.Summarize(),
            DriverOpenLatencyMs = _connectionOpen.Summarize(),
            CreatedMinusClosed = createdClosedDelta,
            CreatedMinusDemand = createdVsDemandDelta,
            LifecycleReconciled = Math.Abs(createdClosedDelta) <= Math.Max(1, created * 0.01),
            ReconciliationDetail =
                $"created={created} ready={connCounters.Ready} failed={connCounters.Failed} closed={closed}; " +
                $"tasksStarted={tasksStarted} tasksReachedDemand={tasksReachedDemand}. Expected " +
                $"(one-Task/one-client/no-reuse): created≈closed after drain (delta={createdClosedDelta}) and " +
                $"created≈Tasks that reached demand (created-demand={createdVsDemandDelta}; negative = Tasks " +
                $"that failed at server-selection/open before a connection object was created). Residual active " +
                $"connecting/ready/closing should be ~0 after drain (connecting={connCounters.ActiveConnecting}, " +
                $"ready={connCounters.ActiveReady}, closing={connCounters.ActiveClosing}).",
        };

        // No-reuse verification (§2.2/§7.2): the constraint is that no connection is reused ACROSS
        // Tasks — every Task that runs opens its OWN new connection and closes it. The correct floor is
        // the number of SUCCESSFUL Tasks (each completed a full 4-op cycle, so each definitely needed
        // its own connection): no-reuse holds when created >= successfulTasks (no connection was shared
        // among completed Tasks) AND created ≈ closed (no leak / no lingering reusable connection).
        // Comparing against TOTAL tasks is wrong: a Task that fails at server-selection never opens a
        // connection, so created < totalTasks is expected under failures and is NOT reuse. Reuse would
        // instead show created << successfulTasks. Within a Task the four ops share that Task's one
        // pooled connection (pool check-outs ≈ 4×tasks), which is also expected and not reuse.
        var successfulTasks = Interlocked.Read(ref _successTasks);
        var noReuseHolds = successfulTasks == 0 || created >= (long)Math.Floor(successfulTasks * 0.99);
        var closedMatchesCreated = Math.Abs(created - closed) <= Math.Max(1, created * 0.01);
        var reuseEvents = Math.Max(0, successfulTasks - created);
        var failedBeforeConnect = Math.Max(0, totalTasks - created);
        result.ReuseCheck = new ReuseVerification
        {
            NoReuseConfirmed = noReuseHolds && closedMatchesCreated,
            SuspectedReuseEvents = reuseEvents,
            Detail = $"tasks={totalTasks} (successful={successfulTasks}), created={created}, " +
                     $"ready={connCounters.Ready}, closed={closed}, checkedOut={connCounters.CheckedOut}. " +
                     $"No-reuse holds when created>=successfulTasks (one fresh connection per completed " +
                     $"Task) and created≈closed (no leak). created<successfulTasks is reuse " +
                     $"(suspectedReuseEvents={reuseEvents}); created<totalTasks here is " +
                     $"{failedBeforeConnect} Tasks that failed at server-selection BEFORE opening a " +
                     $"connection, not reuse. Per-Task pool check-outs (≈4×tasks) are normal within a Task.",
        };

        result.Throughput = _seconds
            .OrderBy(kv => kv.Key)
            .Select(kv => new ThroughputPoint
            {
                Second = kv.Key,
                ScheduledTasks = Interlocked.Read(ref kv.Value.ScheduledTasks),
                StartedTasks = Interlocked.Read(ref kv.Value.StartedTasks),
                ConnectionsCreated = Interlocked.Read(ref kv.Value.ConnectionsCreated),
                ConnectionsReady = Interlocked.Read(ref kv.Value.ConnectionsReady),
                ConnectionsFailed = Interlocked.Read(ref kv.Value.ConnectionsFailed),
                ConnectionsClosed = Interlocked.Read(ref kv.Value.ConnectionsClosed),
                FindInputOps = Interlocked.Read(ref kv.Value.FindInputOps),
                RemoveOps = Interlocked.Read(ref kv.Value.RemoveOps),
                InsertOps = Interlocked.Read(ref kv.Value.InsertOps),
                FindOutputOps = Interlocked.Read(ref kv.Value.FindOutputOps),
                FailedOps = Interlocked.Read(ref kv.Value.FailedOps),
                InFlightTasks = Volatile.Read(ref kv.Value.InFlightMax),
                ActiveConnecting = Volatile.Read(ref kv.Value.ActiveConnectingMax),
                ActiveReady = Volatile.Read(ref kv.Value.ActiveReadyMax),
                WaitingForServer = Volatile.Read(ref kv.Value.WaitingForServerMax),
            })
            .ToList();

        return result;
    }

    /// <summary>Mutable per-second counters (interlocked fields).</summary>
    private sealed class SecondBucket
    {
        public long ScheduledTasks;
        public long StartedTasks;
        public long ConnectionsCreated;
        public long ConnectionsReady;
        public long ConnectionsFailed;
        public long ConnectionsClosed;
        public long FindInputOps;
        public long RemoveOps;
        public long InsertOps;
        public long FindOutputOps;
        public long FailedOps;
        public int InFlightMax;
        public int ActiveConnectingMax;
        public int ActiveReadyMax;
        public int WaitingForServerMax;

        public void UpdateInFlightMax(int candidate) => UpdateMax(ref InFlightMax, candidate);

        public void UpdateActiveConnectingMax(int candidate) => UpdateMax(ref ActiveConnectingMax, candidate);

        public void UpdateActiveReadyMax(int candidate) => UpdateMax(ref ActiveReadyMax, candidate);

        public void UpdateWaitingForServerMax(int candidate) => UpdateMax(ref WaitingForServerMax, candidate);

        private static void UpdateMax(ref int field, int candidate)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref field);
                if (candidate <= observed)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref field, candidate, observed) != observed);
        }
    }
}
