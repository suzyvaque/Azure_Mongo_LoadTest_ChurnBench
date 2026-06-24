using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Bmt.Core.Configuration;

namespace Bmt.Core.Connections;

/// <summary>
/// Creates a brand-new <see cref="TaskConnection"/> (and underlying <see cref="MongoClient"/>) for
/// every Task — the worst-case "1 Task = 1 connection lifecycle" model under test.
///
/// No-reuse rules enforced here (test_instruction.md §2.2/§2.3):
/// <list type="bullet">
///   <item>A new <see cref="MongoClientSettings"/> + <see cref="MongoClient"/> per <see cref="Create"/> call.</item>
///   <item><c>MaxConnectionPoolSize = 1</c>, <c>MinConnectionPoolSize = 0</c> (no warm pool).</item>
///   <item>No static/singleton client, no DI registration, no caching — the factory holds no client.</item>
///   <item>Connection created/ready/closed/checkout events are surfaced to an <see cref="IConnectionEventObserver"/>.</item>
/// </list>
/// NOTE: the official driver's <see cref="MongoClient"/> always owns an internal pool object, so a
/// fully pool-free connection is not natively possible; we constrain it to size 1 and dispose per
/// Task so no pooling/reuse occurs between requests.
/// </summary>
public sealed class TaskConnectionFactory
{
    private readonly string _connectionString;
    private readonly bool _disableRetryWrites;
    private readonly IConnectionEventObserver? _observer;
    private readonly ClientConfig _tuning;
    private int _rrCounter = -1;

    public TaskConnectionFactory(
        TargetKey target,
        string connectionString,
        IConnectionEventObserver? observer = null,
        ClientConfig? tuning = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));
        }

        Target = target;
        _connectionString = connectionString;
        _disableRetryWrites = TargetConnection.RequiresRetryWritesDisabled(target);
        _observer = observer;
        _tuning = tuning ?? new ClientConfig();
    }

    public TargetKey Target { get; }

    /// <summary>Build a factory that resolves the connection string from the target's env var at runtime.</summary>
    public static TaskConnectionFactory FromEnvironment(
        TargetKey target,
        IConnectionEventObserver? observer = null,
        ClientConfig? tuning = null) =>
        new(target, TargetConnection.ResolveConnectionString(target), observer, tuning);

    /// <summary>
    /// Create a fresh single-use connection for one Task. The caller MUST dispose the returned
    /// <see cref="TaskConnection"/> when the Task completes and MUST NOT reuse it.
    /// </summary>
    public TaskConnection Create()
    {
        var settings = BuildSettings();
        var client = new MongoClient(settings);
        return new TaskConnection(client);
    }

    /// <summary>
    /// Build per-request <see cref="MongoClientSettings"/> from the connection string, then force the
    /// no-reuse pool constraints and (for Cosmos RU) <c>RetryWrites=false</c>, and wire event capture.
    /// Exposed for preflight/diagnostics that need to inspect the effective settings.
    /// </summary>
    public MongoClientSettings BuildSettings()
    {
        var settings = MongoClientSettings.FromConnectionString(_connectionString);

        // Hard no-reuse constraints (§2.3): pool of exactly one, never pre-warmed.
        settings.MaxConnectionPoolSize = 1;
        settings.MinConnectionPoolSize = 0;

        // Fail-fast timeouts (applied uniformly to every target). Under the no-reuse model a Task that
        // cannot get a server otherwise holds its slot for the driver default 30 s, snowballing into
        // runaway concurrency that saturates the generator host and masks backend latency.
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(_tuning.ServerSelectionTimeoutMs);
        settings.ConnectTimeout = TimeSpan.FromMilliseconds(_tuning.ConnectTimeoutMs);
        if (_tuning.SocketTimeoutMs > 0)
        {
            settings.SocketTimeout = TimeSpan.FromMilliseconds(_tuning.SocketTimeoutMs);
        }

        // Direct, single-server per-Task connections so each fresh client skips topology discovery +
        // its background heartbeat (SDAM) monitor. At thousands of concurrent clients that per-client
        // monitor (not the DB) becomes the bottleneck. The no-reuse model is unchanged; we just avoid
        // paying topology-discovery overhead per Task. Managed targets (SRV / gateway) are left exactly
        // as their connection string specifies.
        if (_tuning.DirectConnectionForSingleNode)
        {
            if (Target == TargetKey.MongoVm)
            {
                // Single node: connect directly to the one replica-set member.
                settings.DirectConnection = true;
                settings.ReplicaSetName = null;
            }
            else if (Target == TargetKey.MongoShard)
            {
                // Sharded cluster: the connection string lists BOTH mongos routers. Keeping the full
                // sharded topology per Task makes every per-Task client spin up an SDAM monitor for
                // each mongos (~2 monitor threads/client); under churn that explodes into a runaway
                // 48k-thread meltdown on the generator host (see INCIDENT-runaway-concurrency-meltdown).
                // Instead we round-robin: pin each per-Task client to ONE mongos as a DIRECT
                // single-server connection. Tasks alternate between the two mongos seeds, so 2x router
                // fan-out is preserved, while each client pays zero topology-monitoring overhead — the
                // exact same mitigation mongo-vm gets, keeping the two mongo targets methodologically
                // comparable.
                var servers = settings.Servers?.ToList();
                if (servers is { Count: > 0 })
                {
                    var idx = (int)((uint)Interlocked.Increment(ref _rrCounter) % (uint)servers.Count);
                    settings.Server = servers[idx];
                    settings.DirectConnection = true;
                    settings.ReplicaSetName = null;
                }
            }
        }

        // Cosmos RU does not support retryable writes (handoff §3).
        if (_disableRetryWrites)
        {
            settings.RetryWrites = false;
        }

        // Surface connection-monitoring events to the observer (§2.3/§7.2).
        // We ALWAYS assign a fresh ClusterConfigurator instance so each client gets a distinct
        // cluster key — preventing the driver's ClusterRegistry from sharing one cluster across
        // Tasks, which would violate the no-reuse requirement.
        var previous = settings.ClusterConfigurator;
        var observer = _observer;
        settings.ClusterConfigurator = cb =>
        {
            previous?.Invoke(cb);
            if (observer is null)
            {
                return;
            }

            cb.Subscribe<ConnectionCreatedEvent>(e => observer.OnConnectionCreated(e.ConnectionId));
            cb.Subscribe<ConnectionOpenedEvent>(e => observer.OnConnectionReady(e.ConnectionId, e.Duration));
            cb.Subscribe<ConnectionClosedEvent>(e => observer.OnConnectionClosed(e.ConnectionId));
            cb.Subscribe<ConnectionFailedEvent>(e => observer.OnConnectionFailed(e.ConnectionId, e.Exception));
            cb.Subscribe<ConnectionPoolCheckedOutConnectionEvent>(e => observer.OnConnectionCheckedOut(e.ConnectionId));
        };

        return settings;
    }
}
