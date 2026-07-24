namespace Bmt.Core.Configuration;

/// <summary>
/// Per-Task MongoClient tuning applied uniformly by <see cref="Bmt.Core.Connections.TaskConnectionFactory"/>
/// on top of the hard no-reuse constraints (§2.2/§2.3: new client per Task, maxPoolSize=1, minPoolSize=0).
///
/// These knobs do NOT relax the no-reuse model — they only stop a saturated client host from masking
/// backend latency. Under the no-reuse model every Task spins up a fresh client; with the driver's
/// default 30 s server-selection timeout, a Task that cannot get a server holds its slot for 30 s,
/// so transient saturation snowballs into runaway concurrency (observed: 12.8k in-flight, 18k threads,
/// 89% CPU, 86% ServerSelectionTimeout on mongo-vm). Failing fast keeps the measurement about the
/// backend, not the generator host. The same timeouts apply to ALL targets for fairness.
/// </summary>
public sealed class ClientConfig
{
    /// <summary>Server-selection timeout in ms (driver default 30,000). Lower = fail fast under saturation.</summary>
    public int ServerSelectionTimeoutMs { get; set; } = 5_000;

    /// <summary>TCP connect timeout in ms (driver default 30,000).</summary>
    public int ConnectTimeoutMs { get; set; } = 5_000;

    /// <summary>Socket operation timeout in ms; 0 = leave the driver default (no socket timeout).</summary>
    public int SocketTimeoutMs { get; set; }

    /// <summary>
    /// When true, force <c>directConnection=true</c> (and drop any replicaSet name) for the single-node
    /// <c>mongo-vm</c> target. A brand-new client per Task against a <c>replicaSet=rs0</c> URI otherwise
    /// pays full topology discovery + a background heartbeat monitor PER client; at thousands of
    /// concurrent clients that thread/CPU overhead — not the DB — becomes the bottleneck. Connecting
    /// directly to the one node removes that per-client monitor while preserving the no-reuse model.
    /// Only applied to mongo-vm; managed targets (SRV / gateway) are left untouched.
    /// </summary>
    public bool DirectConnectionForSingleNode { get; set; } = true;

    /// <summary>
    /// When true, sets <c>AllowInsecureTls</c> on the per-Task client for the self-managed MongoDB
    /// targets (<c>mongo-shard</c>, <c>mongo-vm</c>). Those endpoints present a certificate that chains
    /// to a PRIVATE CA, so every fresh TLS handshake makes the Windows cert-chain engine build/validate
    /// the chain against the machine trust store. Under the no-reuse model's storm of thousands of
    /// SIMULTANEOUS cold handshakes that validation serializes inside schannel and throttles connection
    /// ESTABLISHMENT to single-digit conn/s while the mongos/mongod CPU sits idle — capping achievable
    /// concurrency far below the server's capacity. Managed DocumentDB presents a publicly-trusted cert
    /// (fast, cached validation) and is unaffected. Skipping chain validation removes this client-side
    /// PKI artifact so the measured concurrency reflects the SERVER's capacity, not the client's TLS
    /// bookkeeping. Only ever applied to the mongo targets; DocumentDB/Cosmos are never altered.
    /// </summary>
    public bool MongoAllowInsecureTls { get; set; }

    public void Validate()
    {
        if (ServerSelectionTimeoutMs <= 0)
        {
            throw new InvalidOperationException("Client.ServerSelectionTimeoutMs must be > 0.");
        }

        if (ConnectTimeoutMs <= 0)
        {
            throw new InvalidOperationException("Client.ConnectTimeoutMs must be > 0.");
        }

        if (SocketTimeoutMs < 0)
        {
            throw new InvalidOperationException("Client.SocketTimeoutMs must be >= 0 (0 = driver default).");
        }
    }
}
