using MongoDB.Driver.Core.Connections;

namespace Bmt.Core.Connections;

/// <summary>
/// Per-Task connection-lifecycle recorder (test_instruction.md §3). In the one-Task/one-client/no-reuse
/// design each Task owns exactly one <see cref="MongoDB.Driver.MongoClient"/> with a size-1 pool, so it
/// opens exactly one physical connection. This lightweight recorder captures that connection's driver
/// timestamps so the Task can compute the two §3 latencies with a correct per-Task correlation:
/// <list type="bullet">
///   <item><c>ConnectionDemandToReadyLatencyMs = ConnectionReadyUtc - ConnectionDemandUtc</c> — the
///     user-observed cold-connection latency (server selection + DNS/SRV + socket + TCP + TLS + hello + auth).</item>
///   <item><c>DriverOpenLatencyMs = ConnectionReadyUtc - DriverConnectionCreatedUtc</c> — isolates the
///     physical connection lifecycle (the driver's reported open duration).</item>
/// </list>
/// It only records the FIRST created/ready it sees (the Task's single connection) and does no I/O, so it
/// is safe to run inside driver event callbacks. Only the two connection events it needs are handled;
/// every other event method is a no-op.
/// </summary>
public sealed class ConnectionLifecycleRecorder : IConnectionEventObserver
{
    private long _demandUtcTicks;
    private long _createdUtcTicks;
    private long _readyUtcTicks;
    private TimeSpan? _driverOpenDuration;

    /// <summary>Stamp the connection-demand instant (set by the Task just before its first operation).</summary>
    public void MarkDemand() => Interlocked.CompareExchange(ref _demandUtcTicks, DateTime.UtcNow.Ticks, 0);

    public DateTime? DemandUtc => TicksToUtc(Interlocked.Read(ref _demandUtcTicks));
    public DateTime? DriverConnectionCreatedUtc => TicksToUtc(Interlocked.Read(ref _createdUtcTicks));
    public DateTime? ConnectionReadyUtc => TicksToUtc(Interlocked.Read(ref _readyUtcTicks));

    /// <summary>Demand → ready latency (ms), or null if the connection never became ready.</summary>
    public double? DemandToReadyMs
    {
        get
        {
            var demand = DemandUtc;
            var ready = ConnectionReadyUtc;
            return demand is { } d && ready is { } r ? Math.Max(0, (r - d).TotalMilliseconds) : null;
        }
    }

    /// <summary>Driver created → ready latency (ms) from the driver's reported open duration, or null.</summary>
    public double? DriverOpenMs => _driverOpenDuration?.TotalMilliseconds;

    public void OnServerSelectionStarted()
    {
    }

    public void OnServerSelectionEnded(bool success)
    {
    }

    public void OnConnectionCreated(ConnectionId connectionId) =>
        Interlocked.CompareExchange(ref _createdUtcTicks, DateTime.UtcNow.Ticks, 0);

    public void OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration)
    {
        if (Interlocked.CompareExchange(ref _readyUtcTicks, DateTime.UtcNow.Ticks, 0) == 0)
        {
            _driverOpenDuration = openDuration;
        }
    }

    public void OnConnectionClosing(ConnectionId connectionId)
    {
    }

    public void OnConnectionClosed(ConnectionId connectionId)
    {
    }

    public void OnConnectionFailed(ConnectionId connectionId, Exception exception)
    {
    }

    public void OnConnectionCheckedOut(ConnectionId connectionId)
    {
    }

    public void OnHandshakeCommand(string commandName, TimeSpan duration, bool success)
    {
    }

    private static DateTime? TicksToUtc(long ticks) =>
        ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
}
