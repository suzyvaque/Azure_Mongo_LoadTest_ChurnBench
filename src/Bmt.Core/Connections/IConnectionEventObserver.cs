using MongoDB.Driver.Core.Connections;

namespace Bmt.Core.Connections;

/// <summary>
/// Sink for driver connection-monitoring events (test_instruction.md §2.3/§7.2). Implementations
/// must be thread-safe: events fire from driver background threads across many concurrent Tasks.
/// </summary>
public interface IConnectionEventObserver
{
    /// <summary>A physical connection object was created (pre-handshake).</summary>
    void OnConnectionCreated(ConnectionId connectionId);

    /// <summary>A connection became ready (TCP + TLS + handshake/auth complete).</summary>
    void OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration);

    /// <summary>A connection was closed / released.</summary>
    void OnConnectionClosed(ConnectionId connectionId);

    /// <summary>A connection failed to open.</summary>
    void OnConnectionFailed(ConnectionId connectionId, Exception exception);

    /// <summary>
    /// A pool checkout occurred. In a no-reuse run this should equal the number of Tasks
    /// (one fresh connection per Task); a higher count hints at unexpected reuse/retries.
    /// </summary>
    void OnConnectionCheckedOut(ConnectionId connectionId);
}
