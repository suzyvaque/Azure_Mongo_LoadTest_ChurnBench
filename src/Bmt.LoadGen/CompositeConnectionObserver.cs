using Bmt.Core.Connections;
using MongoDB.Driver.Core.Connections;

namespace Bmt.LoadGen;

/// <summary>
/// Fans connection-monitoring events out to several <see cref="IConnectionEventObserver"/> sinks so the
/// single factory hook can feed both the <see cref="Bmt.Core.Connections.ConnectionEventCounters"/>
/// (totals + reuse verification) and the <see cref="MetricsCollector"/> (connection-open latency digest).
/// </summary>
public sealed class CompositeConnectionObserver : IConnectionEventObserver
{
    private readonly IConnectionEventObserver[] _sinks;

    public CompositeConnectionObserver(params IConnectionEventObserver[] sinks) =>
        _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));

    public void OnConnectionCreated(ConnectionId connectionId)
    {
        foreach (var s in _sinks)
        {
            s.OnConnectionCreated(connectionId);
        }
    }

    public void OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration)
    {
        foreach (var s in _sinks)
        {
            s.OnConnectionReady(connectionId, openDuration);
        }
    }

    public void OnConnectionClosed(ConnectionId connectionId)
    {
        foreach (var s in _sinks)
        {
            s.OnConnectionClosed(connectionId);
        }
    }

    public void OnConnectionFailed(ConnectionId connectionId, Exception exception)
    {
        foreach (var s in _sinks)
        {
            s.OnConnectionFailed(connectionId, exception);
        }
    }

    public void OnConnectionCheckedOut(ConnectionId connectionId)
    {
        foreach (var s in _sinks)
        {
            s.OnConnectionCheckedOut(connectionId);
        }
    }
}
