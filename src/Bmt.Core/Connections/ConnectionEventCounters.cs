using MongoDB.Driver.Core.Connections;

namespace Bmt.Core.Connections;

/// <summary>
/// Thread-safe, allocation-free counter sink for connection events. Tracks the totals needed for
/// the §7.2 connection metrics and the reuse-verification check (created ≈ closed ≈ checked-out ≈
/// number of Tasks in a correct no-reuse run).
/// </summary>
public sealed class ConnectionEventCounters : IConnectionEventObserver
{
    private long _created;
    private long _ready;
    private long _closed;
    private long _failed;
    private long _checkedOut;
    private long _openDurationTicks;
    private long _openDurationSamples;

    public long Created => Interlocked.Read(ref _created);
    public long Ready => Interlocked.Read(ref _ready);
    public long Closed => Interlocked.Read(ref _closed);
    public long Failed => Interlocked.Read(ref _failed);
    public long CheckedOut => Interlocked.Read(ref _checkedOut);

    /// <summary>Mean connection-open (handshake) duration across all <c>OnConnectionReady</c> samples.</summary>
    public TimeSpan MeanOpenDuration
    {
        get
        {
            var samples = Interlocked.Read(ref _openDurationSamples);
            if (samples == 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromTicks(Interlocked.Read(ref _openDurationTicks) / samples);
        }
    }

    public void OnConnectionCreated(ConnectionId connectionId) => Interlocked.Increment(ref _created);

    public void OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration)
    {
        Interlocked.Increment(ref _ready);
        if (openDuration is { } d)
        {
            Interlocked.Add(ref _openDurationTicks, d.Ticks);
            Interlocked.Increment(ref _openDurationSamples);
        }
    }

    public void OnConnectionClosed(ConnectionId connectionId) => Interlocked.Increment(ref _closed);

    public void OnConnectionFailed(ConnectionId connectionId, Exception exception) =>
        Interlocked.Increment(ref _failed);

    public void OnConnectionCheckedOut(ConnectionId connectionId) =>
        Interlocked.Increment(ref _checkedOut);
}
