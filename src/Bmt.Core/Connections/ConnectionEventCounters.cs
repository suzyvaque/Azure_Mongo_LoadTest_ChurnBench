using System.Collections.Concurrent;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace Bmt.Core.Connections;

/// <summary>
/// Authoritative connection-lifecycle source (test_instruction.md §3/§7.2). Driver connection-monitoring
/// events are the PRIMARY evidence of connection behavior — never Task counts or raw host TCP sockets.
/// This sink tracks the cumulative lifecycle counters and the live gauges the comparison needs, and
/// derives the logical states from them.
///
/// <para><b>Counter semantics</b> (each event is counted exactly once):</para>
/// <list type="bullet">
///   <item><c>WaitingForServer</c> (gauge): operations currently in driver server-selection — between
///     <c>ClusterSelectingServer</c> and <c>ClusterSelectedServer/Failed</c>. Not yet a physical connection.</item>
///   <item><c>ConnectionsCreated</c> (cumulative): physical connection objects created (<c>ConnectionCreated</c>,
///     pre-handshake). In the one-Task/one-client/no-reuse design ≈ one per Task that reaches connection demand.</item>
///   <item><c>ConnectionsReady</c> (cumulative): connections that completed TCP+TLS+hello+auth (<c>ConnectionOpened</c>).</item>
///   <item><c>ConnectionsFailed</c> (cumulative): connections that failed to open (<c>ConnectionFailed</c>).</item>
///   <item><c>ConnectionsClosed</c> (cumulative): connections closed/released (<c>ConnectionClosed</c>).</item>
///   <item><c>ActiveConnecting</c> (gauge): created but not yet ready/failed/closed (the <c>Connecting</c> state).</item>
///   <item><c>ActiveReady</c> (gauge): ready and not yet closed (the <c>Ready</c> state).</item>
/// </list>
/// Peaks (<see cref="PeakActiveConnecting"/>, <see cref="PeakActiveReady"/>, <see cref="PeakWaitingForServer"/>)
/// retain the high-water mark of each gauge for the §3 concurrency evidence.
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
    private long _helloCommands;
    private long _authCommands;

    // Live gauges (derived from the per-connection state machine) + their high-water marks.
    private int _activeConnecting;
    private int _activeReady;
    private int _activeClosing;
    private int _waitingForServer;
    private int _peakActiveConnecting;
    private int _peakActiveReady;
    private int _peakActiveClosing;
    private int _peakWaitingForServer;

    // Per-connection state so a close/fail can correctly decrement the right gauge (a connection can be
    // closed while still Connecting, or after becoming Ready). Entries are removed on close/fail so the
    // map never retains connections past their lifetime (bounded memory under churn).
    private const byte StateConnecting = 1;
    private const byte StateReady = 2;
    private const byte StateClosing = 3;
    private readonly ConcurrentDictionary<(ServerId Server, long Local), byte> _state = new();

    public long Created => Interlocked.Read(ref _created);
    public long Ready => Interlocked.Read(ref _ready);
    public long Closed => Interlocked.Read(ref _closed);
    public long Failed => Interlocked.Read(ref _failed);
    public long CheckedOut => Interlocked.Read(ref _checkedOut);

    public int ActiveConnecting => Volatile.Read(ref _activeConnecting);
    public int ActiveReady => Volatile.Read(ref _activeReady);
    public int ActiveClosing => Volatile.Read(ref _activeClosing);
    public int WaitingForServer => Volatile.Read(ref _waitingForServer);
    public int PeakActiveConnecting => Volatile.Read(ref _peakActiveConnecting);
    public int PeakActiveReady => Volatile.Read(ref _peakActiveReady);
    public int PeakActiveClosing => Volatile.Read(ref _peakActiveClosing);
    public int PeakWaitingForServer => Volatile.Read(ref _peakWaitingForServer);

    /// <summary>Count of <c>hello</c>/<c>isMaster</c> wire-negotiation commands seen during handshakes.</summary>
    public long HelloCommands => Interlocked.Read(ref _helloCommands);

    /// <summary>Count of SCRAM <c>saslStart</c>/<c>saslContinue</c> auth commands seen during handshakes.</summary>
    public long AuthCommands => Interlocked.Read(ref _authCommands);

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

    public void OnServerSelectionStarted() =>
        UpdatePeak(ref _peakWaitingForServer, Interlocked.Increment(ref _waitingForServer));

    public void OnServerSelectionEnded(bool success) =>
        Interlocked.Decrement(ref _waitingForServer);

    public void OnConnectionCreated(ConnectionId connectionId)
    {
        Interlocked.Increment(ref _created);
        _state[Key(connectionId)] = StateConnecting;
        UpdatePeak(ref _peakActiveConnecting, Interlocked.Increment(ref _activeConnecting));
    }

    public void OnConnectionReady(ConnectionId connectionId, TimeSpan? openDuration)
    {
        Interlocked.Increment(ref _ready);
        if (openDuration is { } d)
        {
            Interlocked.Add(ref _openDurationTicks, d.Ticks);
            Interlocked.Increment(ref _openDurationSamples);
        }

        // Connecting -> Ready.
        if (_state.TryGetValue(Key(connectionId), out var st) && st == StateConnecting)
        {
            Interlocked.Decrement(ref _activeConnecting);
        }

        _state[Key(connectionId)] = StateReady;
        UpdatePeak(ref _peakActiveReady, Interlocked.Increment(ref _activeReady));
    }

    public void OnConnectionClosing(ConnectionId connectionId)
    {
        // Ready/Connecting -> Closing. Move the gauge from its current state into ActiveClosing so the
        // connection is not still counted as Ready while it is being torn down.
        var key = Key(connectionId);
        if (_state.TryGetValue(key, out var st))
        {
            if (st == StateReady)
            {
                Interlocked.Decrement(ref _activeReady);
            }
            else if (st == StateConnecting)
            {
                Interlocked.Decrement(ref _activeConnecting);
            }
            else
            {
                return; // already Closing — no double transition
            }

            _state[key] = StateClosing;
            UpdatePeak(ref _peakActiveClosing, Interlocked.Increment(ref _activeClosing));
        }
    }

    public void OnConnectionClosed(ConnectionId connectionId)
    {
        Interlocked.Increment(ref _closed);
        if (_state.TryRemove(Key(connectionId), out var st))
        {
            switch (st)
            {
                case StateClosing: Interlocked.Decrement(ref _activeClosing); break;
                case StateReady: Interlocked.Decrement(ref _activeReady); break;
                case StateConnecting: Interlocked.Decrement(ref _activeConnecting); break;
            }
        }
    }

    public void OnConnectionFailed(ConnectionId connectionId, Exception exception)
    {
        // Idempotent: the driver may emit both ConnectionOpeningFailedEvent and ConnectionFailedEvent (and
        // possibly a trailing ConnectionClosedEvent) for the same failed connection. Only the FIRST event
        // that still finds the connection in the state map accounts for the failure + gauge decrement.
        if (_state.TryRemove(Key(connectionId), out var st))
        {
            Interlocked.Increment(ref _failed);
            switch (st)
            {
                case StateClosing: Interlocked.Decrement(ref _activeClosing); break;
                case StateReady: Interlocked.Decrement(ref _activeReady); break;
                case StateConnecting: Interlocked.Decrement(ref _activeConnecting); break;
            }
        }
    }

    public void OnConnectionCheckedOut(ConnectionId connectionId) =>
        Interlocked.Increment(ref _checkedOut);

    public void OnHandshakeCommand(string commandName, TimeSpan duration, bool success)
    {
        if (IsAuthCommand(commandName))
        {
            Interlocked.Increment(ref _authCommands);
        }
        else
        {
            Interlocked.Increment(ref _helloCommands);
        }
    }

    /// <summary>True for SCRAM auth commands (<c>saslStart</c>/<c>saslContinue</c>); false for hello/isMaster.</summary>
    public static bool IsAuthCommand(string commandName) =>
        commandName.StartsWith("sasl", StringComparison.OrdinalIgnoreCase);

    private static (ServerId, long) Key(ConnectionId id) => (id.ServerId, id.LongLocalValue);

    private static void UpdatePeak(ref int peak, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref peak);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref peak, candidate, observed) != observed);
    }
}
