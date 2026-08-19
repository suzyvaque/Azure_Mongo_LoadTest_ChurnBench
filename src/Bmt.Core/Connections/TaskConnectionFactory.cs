using DnsClient;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
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
    // The commands the MongoDB driver issues while establishing a NEW connection: wire negotiation
    // (hello/isMaster) and SCRAM authentication (saslStart/saslContinue). Everything else is workload.
    private static readonly HashSet<string> HandshakeCommands =
        new(StringComparer.OrdinalIgnoreCase) { "hello", "isMaster", "saslStart", "saslContinue" };

    // DocumentDB's connection string uses the mongodb+srv scheme. `MongoClientSettings.FromConnectionString`
    // does NOT eagerly resolve the SRV record — it just parses the string (Scheme stays MongoDBPlusSrv,
    // Servers stays the un-resolved seed host with the DNS-port placeholder 53). The actual
    // "_mongodb._tcp.<host>" SRV lookup happens later, inside the driver's own cluster/monitor
    // construction — i.e. once per `new MongoClient(...)`, which under the no-reuse model means once per
    // Task. We resolve the SRV record OURSELVES (via DnsClient, same package Bmt.LoadGen's
    // TargetEndpointResolver already uses) on a periodic cache, and rewrite the cached settings to a
    // PLAIN mongodb:// scheme pointed at the resolved host:port — this both removes the redundant
    // per-Task DNS lookup AND makes DirectConnection legal (the driver rejects DirectConnection on an
    // SRV-scheme client: "SRV cannot be used with direct connections"), letting DocumentDB skip its
    // per-client SDAM topology monitor exactly like the mongo-vm/mongo-shard mitigations below.
    //
    // INCIDENT (documentdb private-endpoint connection-storm, two compounding causes):
    //  1. TLS certificate-revocation checks (see the CheckCertificateRevocation=false fix below) serialized
    //     under concurrent cold handshakes — 40 concurrent handshakes each took ~29.5s vs <100ms sequential.
    //  2. Even after fixing (1), a sustained 135 tasks/s steady campaign still degraded badly (mean
    //     successful ~39/s, ServerSelectionTimeout dominant, p99 cycle latency in the hundreds of
    //     seconds) while Azure Monitor showed the DATABASE ITSELF idle (~0.4-0.6% CPU) throughout — a
    //     purely client-side pile-up. This matches the documented mongo-shard "runaway concurrency
    //     meltdown" mechanism (see results/run-20260624-1host-singleop/INCIDENT-runaway-concurrency-
    //     meltdown.md): every live per-Task MongoClient that is NOT a direct single-server connection
    //     keeps a background SDAM heartbeat monitor running for as long as that client object is alive;
    //     once anything makes ops even slightly slower than expected, live-client count balloons (bounded
    //     only by MaxConcurrentTasks), and thousands of accumulating monitors/threads snowball into the
    //     observed tail-latency blowup. mongo-vm/mongo-shard were already fixed this way; DocumentDB was
    //     originally assumed not to need it because it exposes a single gateway endpoint — true, but the
    //     mitigation still requires DirectConnection=true, which needs the SRV rewrite above to be legal.
    //     Confirmed via TargetEndpointResolver logs that this cluster's SRV record resolves to exactly ONE
    //     host regardless of shardCount=2 (shard routing happens inside that one gateway node, invisible
    //     to the client), so DirectConnection=true does not defeat any client-visible routing.
    private static readonly TimeSpan SrvCacheRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly string _connectionString;
    private readonly bool _disableRetryWrites;
    private readonly IConnectionEventObserver? _observer;
    private readonly RetryEventCounters? _retryCounters;
    private readonly ClientConfig _tuning;
    private readonly object _srvCacheLock = new();
    private volatile MongoClientSettings? _cachedResolvedSettings;
    private DateTime _cachedResolvedAtUtc = DateTime.MinValue;
    private int _rrCounter = -1;

    public TaskConnectionFactory(
        TargetKey target,
        string connectionString,
        IConnectionEventObserver? observer = null,
        ClientConfig? tuning = null,
        RetryEventCounters? retryCounters = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));
        }

        Target = target;
        _connectionString = connectionString;
        _disableRetryWrites = TargetConnection.RequiresRetryWritesDisabled(target);
        _observer = observer;
        _retryCounters = retryCounters;
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
    /// <see cref="TaskConnection"/> when the Task completes and MUST NOT reuse it. A per-Task
    /// <see cref="ConnectionLifecycleRecorder"/> is wired in so the Task can measure demand-to-ready and
    /// driver-open latency with a correct per-Task correlation (§3).
    /// </summary>
    public TaskConnection Create()
    {
        var recorder = new ConnectionLifecycleRecorder();
        var settings = BuildSettings(recorder);
        var client = new MongoClient(settings);
        return new TaskConnection(client, recorder);
    }

    /// <summary>
    /// Build per-request <see cref="MongoClientSettings"/> from the connection string, then force the
    /// no-reuse pool constraints and (for Cosmos RU) <c>RetryWrites=false</c>, and wire event capture.
    /// Exposed for preflight/diagnostics that need to inspect the effective settings.
    /// <paramref name="perTaskObserver"/> (optional) receives the same events as the shared observer, so a
    /// per-Task recorder can correlate this client's single connection.
    /// </summary>
    public MongoClientSettings BuildSettings(IConnectionEventObserver? perTaskObserver = null)
    {
        var settings = ResolveBaseSettings();

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
        // paying topology-discovery overhead per Task.
        //
        // ITEM 5 — ACCESS-PATH ASYMMETRY (must be disclosed in every result summary): mongo-vm/mongo-shard
        // pin to a direct single-server connection because their topology is otherwise MULTIPLE mongos
        // routers (2 SDAM monitors/client). DocumentDB is a SINGLE managed SRV/gateway endpoint — no
        // multi-router fan-out to preserve — but it turns out the SAME per-client SDAM-monitor cost still
        // applies to ANY non-direct client regardless of router count, and still snowballs into a
        // client-side meltdown at sustained high concurrency (see the class-level SRV-storm/meltdown
        // comment above `SrvCacheRefreshInterval`). DocumentDB's connection string is first rewritten to a
        // plain mongodb:// scheme pointed at its one resolved gateway host (see
        // <see cref="ResolveSrvToDirectSettings"/>), which makes DirectConnection legal here too. Internal
        // shard routing happens entirely inside that one gateway node, invisible to the client, so this
        // does NOT defeat any client-visible routing — unlike mongo-shard there is no router fan-out being
        // given up, so this is a pure overhead removal, not an access-path trade-off. Build-RunSummary.ps1
        // still emits the general access-path-asymmetry caveat since mongo-vm/mongo-shard's direct pin
        // trades away router fan-out while DocumentDB's does not.
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
            else if (Target == TargetKey.DocumentDb && settings.Scheme == ConnectionStringScheme.MongoDB)
            {
                // Only legal once ResolveSrvToDirectSettings has successfully rewritten the scheme away
                // from SRV (the driver throws "SRV cannot be used with direct connections" otherwise). On
                // the rare fallback path (SRV resolution failed and Scheme is still MongoDBPlusSrv), skip
                // this and let the driver fall back to its own per-client SRV resolution instead of
                // throwing.
                settings.DirectConnection = true;
                settings.ReplicaSetName = null;
            }
        }

        // Cosmos RU does not support retryable writes (handoff §3). DocumentDB and self-managed mongo
        // DO support them, so retryable writes are FORCED ON for documentdb here — independent of the
        // connection-string flag (the production env string historically carried retrywrites=false).
        // Retryable failures that trigger a retry are counted via the retry-event subscription below and
        // surfaced as RetryStats for later cross-checking against throttling/429 metrics.
        if (_disableRetryWrites)
        {
            settings.RetryWrites = false;
        }
        else if (Target == TargetKey.DocumentDb)
        {
            settings.RetryWrites = true;
        }

        // Self-managed MongoDB (mongo-shard/mongo-vm) presents a PRIVATE-CA certificate. Under the
        // no-reuse storm of thousands of simultaneous cold TLS handshakes, Windows schannel serializes
        // the per-handshake cert-chain validation, throttling connection establishment to single-digit
        // conn/s while the servers sit idle — capping achievable concurrency. Skipping chain validation
        // for these targets removes that client-side PKI bottleneck. Never applied to managed targets.
        if (_tuning.MongoAllowInsecureTls && (Target == TargetKey.MongoShard || Target == TargetKey.MongoVm))
        {
            settings.AllowInsecureTls = true;
        }

        // ALL targets whose certificate chains to a PUBLIC CA (DocumentDB, Cosmos RU) are reached here
        // only via a PRIVATE ENDPOINT with no internet egress guaranteed for the client VNet. Schannel's
        // certificate-chain build still validates hostname/chain/trust normally, but ALSO tries to fetch
        // CRL/OCSP revocation data over the internet per handshake. A single cold handshake absorbs that
        // fetch in a few hundred ms (then caches), but under the no-reuse model's storm of tens of
        // SIMULTANEOUS cold handshakes, concurrent revocation fetches serialize/contend and each one can
        // block for ~30 s before the connection completes — measured directly: 40 concurrent handshakes to
        // docdb-dbtest-hpc-1 each took ~29.5 s (vs <100 ms sequentially), which snowballs into the
        // ServerSelectionTimeout storm this ClientConfig's fail-fast timeouts are meant to avoid. This is
        // the standard Microsoft-recommended mitigation for PaaS-behind-private-endpoint TLS clients:
        // disable ONLY the revocation check, keeping full chain + hostname validation intact (unlike
        // AllowInsecureTls above, which bypasses validation entirely and is reserved for the private-CA
        // mongo targets).
        settings.SslSettings = new SslSettings { CheckCertificateRevocation = false };

        // Surface connection-monitoring events to the observer(s) (§2.3/§7.2/§3). The shared observer
        // aggregates campaign-wide lifecycle counters; the optional per-Task recorder correlates THIS
        // client's single connection for demand-to-ready timing. We ALWAYS assign a fresh
        // ClusterConfigurator instance so each client gets a distinct cluster key — preventing the
        // driver's ClusterRegistry from sharing one cluster across Tasks (which would violate no-reuse).
        var previous = settings.ClusterConfigurator;
        var observers = perTaskObserver is null
            ? (_observer is null ? Array.Empty<IConnectionEventObserver>() : new[] { _observer })
            : (_observer is null ? new[] { perTaskObserver } : new[] { _observer, perTaskObserver });
        settings.ClusterConfigurator = cb =>
        {
            previous?.Invoke(cb);
            if (observers.Length == 0)
            {
                return;
            }

            // Server selection ("waiting for server") — the state BEFORE any physical connection exists.
            cb.Subscribe<ClusterSelectingServerEvent>(_ => Notify(observers, o => o.OnServerSelectionStarted()));
            cb.Subscribe<ClusterSelectedServerEvent>(_ => Notify(observers, o => o.OnServerSelectionEnded(true)));
            cb.Subscribe<ClusterSelectingServerFailedEvent>(_ => Notify(observers, o => o.OnServerSelectionEnded(false)));

            cb.Subscribe<ConnectionCreatedEvent>(e => Notify(observers, o => o.OnConnectionCreated(e.ConnectionId)));
            cb.Subscribe<ConnectionOpenedEvent>(e => Notify(observers, o => o.OnConnectionReady(e.ConnectionId, e.Duration)));
            cb.Subscribe<ConnectionClosingEvent>(e => Notify(observers, o => o.OnConnectionClosing(e.ConnectionId)));
            cb.Subscribe<ConnectionClosedEvent>(e => Notify(observers, o => o.OnConnectionClosed(e.ConnectionId)));
            // "Failed to open" is ConnectionOpeningFailedEvent (DNS/TCP/TLS/handshake). ConnectionFailedEvent
            // covers I/O failures on an already-open connection. Both route to OnConnectionFailed, which is
            // idempotent (accounts the failure once, via the per-connection state map).
            cb.Subscribe<ConnectionOpeningFailedEvent>(e => Notify(observers, o => o.OnConnectionFailed(e.ConnectionId, e.Exception)));
            cb.Subscribe<ConnectionFailedEvent>(e => Notify(observers, o => o.OnConnectionFailed(e.ConnectionId, e.Exception)));
            cb.Subscribe<ConnectionPoolCheckedOutConnectionEvent>(e => Notify(observers, o => o.OnConnectionCheckedOut(e.ConnectionId)));

            // Handshake/auth timing (§7.2): the driver emits a command event for every command, including
            // the connection-establishment handshake (hello/isMaster wire negotiation + SCRAM
            // saslStart/saslContinue auth). We filter to just those so the run can report auth cost
            // separately from the raw TCP+TLS portion of connection-open. Command bodies are redacted by
            // the driver for security; only the name + duration are used here. Applied uniformly to every
            // target (DocumentDB authenticates with SCRAM-SHA-256 too), so the breakdown stays comparable.
            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                if (IsHandshakeCommand(e.CommandName))
                {
                    Notify(observers, o => o.OnHandshakeCommand(e.CommandName, e.Duration, success: true));
                }
            });
            cb.Subscribe<CommandFailedEvent>(e =>
            {
                if (IsHandshakeCommand(e.CommandName))
                {
                    Notify(observers, o => o.OnHandshakeCommand(e.CommandName, e.Duration, success: false));
                }
                else
                {
                    // Non-handshake (workload) command failure: record it for the retry taxonomy so a run
                    // can later correlate retryable conditions with throttling/429s. Best-effort, O(1).
                    _retryCounters?.OnCommandFailed(e.Failure);
                }
            });
        };

        return settings;
    }

    /// <summary>
    /// Parse <see cref="_connectionString"/> into a fresh <see cref="MongoClientSettings"/> base, per Task
    /// for every target EXCEPT DocumentDB. For DocumentDB (mongodb+srv scheme), reuse a periodically
    /// refreshed cached, ALREADY-SRV-RESOLVED settings object via <see cref="MongoClientSettings.Clone"/>
    /// — see the class-level SRV-storm/meltdown comment on <see cref="SrvCacheRefreshInterval"/>.
    /// </summary>
    private MongoClientSettings ResolveBaseSettings()
    {
        if (Target != TargetKey.DocumentDb)
        {
            return MongoClientSettings.FromConnectionString(_connectionString);
        }

        var cached = _cachedResolvedSettings;
        if (cached is not null && DateTime.UtcNow - _cachedResolvedAtUtc < SrvCacheRefreshInterval)
        {
            return cached.Clone();
        }

        lock (_srvCacheLock)
        {
            // Re-check inside the lock: another Task may have refreshed while we were waiting.
            cached = _cachedResolvedSettings;
            if (cached is not null && DateTime.UtcNow - _cachedResolvedAtUtc < SrvCacheRefreshInterval)
            {
                return cached.Clone();
            }

            var fresh = ResolveSrvToDirectSettings(_connectionString);
            _cachedResolvedSettings = fresh;
            _cachedResolvedAtUtc = DateTime.UtcNow;
            return fresh.Clone();
        }
    }

    /// <summary>
    /// Parse the connection string, then — if it uses <c>mongodb+srv</c> — resolve the
    /// <c>_mongodb._tcp.&lt;host&gt;</c> SRV record ourselves (once) and rewrite the settings to a plain
    /// <c>mongodb://</c> scheme pointed directly at the resolved host:port, with TLS forced on (the
    /// <c>+srv</c> scheme implies TLS by default; the plain scheme does not). This both removes the
    /// per-Task DNS cost AND makes <see cref="MongoClientSettings.DirectConnection"/> legal afterward
    /// (the driver rejects DirectConnection while Scheme is still SRV). On any resolution failure, the
    /// original (still-SRV) settings are returned unchanged so the driver falls back to its own
    /// (slower but correct) per-client SRV resolution rather than failing the Task outright.
    /// </summary>
    private static MongoClientSettings ResolveSrvToDirectSettings(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        if (settings.Scheme != ConnectionStringScheme.MongoDBPlusSrv)
        {
            return settings;
        }

        try
        {
            var srvHost = settings.Servers.Single().Host;
            var dns = new LookupClient();
            var result = dns.Query($"_mongodb._tcp.{srvHost}", QueryType.SRV);
            var srv = result.Answers.SrvRecords().FirstOrDefault();
            if (srv is not null)
            {
                settings.Scheme = ConnectionStringScheme.MongoDB;
                settings.Servers = new[] { new MongoServerAddress(srv.Target.Value.TrimEnd('.'), srv.Port) };
                settings.UseTls = true;
            }
        }
        catch (Exception)
        {
            // Fall back to the (unmodified, still-SRV) parse — the driver will do its own slower
            // per-client SRV resolution rather than the Task failing outright.
        }

        return settings;
    }

    private static void Notify(IConnectionEventObserver[] observers, Action<IConnectionEventObserver> action)
    {
        foreach (var o in observers)
        {
            action(o);
        }
    }

    /// <summary>True when the command is part of connection establishment (wire negotiation or SCRAM auth).</summary>
    private static bool IsHandshakeCommand(string commandName) => HandshakeCommands.Contains(commandName);
}
