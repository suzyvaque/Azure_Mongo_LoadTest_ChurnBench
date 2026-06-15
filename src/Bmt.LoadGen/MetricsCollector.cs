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

    private readonly ConcurrentDictionary<BmtErrorType, long> _errors = new();
    private readonly ConcurrentDictionary<int, SecondBucket> _seconds = new();

    private long _totalTasks;
    private long _successTasks;
    private long _failTasks;
    private long _totalOps;
    private long _successOps;
    private long _failOps;
    private int _inFlight;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Reset the run clock to "now" (call immediately before the timed phase begins).</summary>
    public void StartClock() => _clock.Restart();

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Called when a Task begins (a fresh connection is about to be opened).</summary>
    public void OnTaskStart()
    {
        Interlocked.Increment(ref _totalTasks);
        var now = Interlocked.Increment(ref _inFlight);
        var b = Bucket();
        Interlocked.Increment(ref b.ConnectionsCreated);
        b.UpdateInFlightMax(now);
    }

    /// <summary>Called when a Task finishes (its connection has been released).</summary>
    public void OnTaskEnd(bool success, double cycleMs)
    {
        Interlocked.Decrement(ref _inFlight);
        var b = Bucket();
        Interlocked.Increment(ref b.ConnectionsClosed);
        _cycle.Record(cycleMs);
        if (success)
        {
            Interlocked.Increment(ref _successTasks);
        }
        else
        {
            Interlocked.Increment(ref _failTasks);
        }
    }

    public void RecordClientCreate(double ms) => _clientCreate.Record(ms);

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

    // ---- IConnectionEventObserver: feed connection-open (handshake/TLS/auth) latency into the digest.
    // Other event kinds are tallied by ConnectionEventCounters; here we only need the open duration.
    void IConnectionEventObserver.OnConnectionCreated(ConnectionId connectionId)
    {
    }

    void IConnectionEventObserver.OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration)
    {
        if (openDuration is { } d)
        {
            _connectionOpen.Record(d.TotalMilliseconds);
        }
    }

    void IConnectionEventObserver.OnConnectionClosed(ConnectionId connectionId)
    {
    }

    void IConnectionEventObserver.OnConnectionFailed(ConnectionId connectionId, Exception exception)
    {
    }

    void IConnectionEventObserver.OnConnectionCheckedOut(ConnectionId connectionId)
    {
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
            },
            TaskCycleLatencyMs = _cycle.Summarize(),
            ConnectionOpenMs = _connectionOpen.Summarize(),
            ClientCreateMs = _clientCreate.Summarize(),
            OperationLatencyMs = new Dictionary<string, LatencySummary>
            {
                [OpNames.FindInput] = _findInput.Summarize(),
                [OpNames.Remove] = _remove.Summarize(),
                [OpNames.Insert] = _insert.Summarize(),
                [OpNames.FindOutput] = _findOutput.Summarize(),
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

        // No-reuse verification (§2.2/§7.2): the constraint is that no connection is reused ACROSS
        // Tasks — i.e. every Task opens exactly one NEW connection and closes it (created ≈ closed ≈
        // tasks). Within a single Task the four ops legitimately reuse that Task's one pooled
        // connection, so pool check-outs (≈ 4 × tasks) are EXPECTED and are not a reuse violation.
        var createdMatchesTasks = totalTasks == 0 || Math.Abs(created - totalTasks) <= Math.Max(1, totalTasks * 0.01);
        var closedMatchesCreated = Math.Abs(created - closed) <= Math.Max(1, created * 0.01);
        var reuseEvents = Math.Max(0, totalTasks - created);
        result.ReuseCheck = new ReuseVerification
        {
            NoReuseConfirmed = createdMatchesTasks && closedMatchesCreated,
            SuspectedReuseEvents = reuseEvents,
            Detail = $"tasks={totalTasks}, created={created}, ready={connCounters.Ready}, closed={closed}, " +
                     $"checkedOut={connCounters.CheckedOut}. Expected created≈closed≈tasks " +
                     $"(per-Task pool check-outs ≈ 4×tasks are normal within a Task and not reuse).",
        };

        result.Throughput = _seconds
            .OrderBy(kv => kv.Key)
            .Select(kv => new ThroughputPoint
            {
                Second = kv.Key,
                ConnectionsCreated = Interlocked.Read(ref kv.Value.ConnectionsCreated),
                ConnectionsClosed = Interlocked.Read(ref kv.Value.ConnectionsClosed),
                FindInputOps = Interlocked.Read(ref kv.Value.FindInputOps),
                RemoveOps = Interlocked.Read(ref kv.Value.RemoveOps),
                InsertOps = Interlocked.Read(ref kv.Value.InsertOps),
                FindOutputOps = Interlocked.Read(ref kv.Value.FindOutputOps),
                FailedOps = Interlocked.Read(ref kv.Value.FailedOps),
                InFlightTasks = Volatile.Read(ref kv.Value.InFlightMax),
            })
            .ToList();

        return result;
    }

    /// <summary>Mutable per-second counters (interlocked fields).</summary>
    private sealed class SecondBucket
    {
        public long ConnectionsCreated;
        public long ConnectionsClosed;
        public long FindInputOps;
        public long RemoveOps;
        public long InsertOps;
        public long FindOutputOps;
        public long FailedOps;
        public int InFlightMax;

        public void UpdateInFlightMax(int candidate)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref InFlightMax);
                if (candidate <= observed)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref InFlightMax, candidate, observed) != observed);
        }
    }
}
