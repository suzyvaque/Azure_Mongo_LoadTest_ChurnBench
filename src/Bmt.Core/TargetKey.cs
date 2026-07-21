namespace Bmt.Core;

/// <summary>
/// The benchmark targets. CLI names follow test_instruction.md §5
/// (<c>cosmos-ru</c> / <c>documentdb</c> / <c>mongo-vm</c>); each maps to a handoff env var.
/// <c>mongo-shard</c> is the sharded-cluster sibling of <c>mongo-vm</c> (mongos routers in front of
/// multiple shards) used to answer the IaaS-scaling question without disturbing the single-node target.
/// </summary>
public enum TargetKey
{
    CosmosRu,
    DocumentDb,
    MongoVm,
    MongoShard,
}

/// <summary>
/// Maps a <see cref="TargetKey"/> to its CLI name and the User/Process env var that holds the
/// (secret) connection string. Secrets are never stored in the repo — they are read at runtime.
/// </summary>
public static class TargetConnection
{
    /// <summary>The canonical CLI <c>--target</c> token (test_instruction.md §5).</summary>
    public static string CliName(TargetKey target) => target switch
    {
        TargetKey.CosmosRu => "cosmos-ru",
        TargetKey.DocumentDb => "documentdb",
        TargetKey.MongoVm => "mongo-vm",
        TargetKey.MongoShard => "mongo-shard",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>The env var holding the credentialed connection string (handoff §2).</summary>
    public static string EnvVarName(TargetKey target) => target switch
    {
        TargetKey.CosmosRu => "BMT_CONN_COSMOS",
        TargetKey.DocumentDb => "BMT_CONN",
        TargetKey.MongoVm => "BMT_CONN_MONGO",
        TargetKey.MongoShard => "BMT_CONN_MONGO_SHARD",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>
    /// The env var holding the <c>bmt_monitor</c> (clusterMonitor) connection string used for the
    /// server-side <c>serverStatus</c>/<c>connPoolStats</c> capture. Only the self-managed mongo
    /// targets have one; PaaS targets return <c>null</c>. Both mongo targets share the single
    /// <c>BMT_CONN_MONGO_MONITOR</c> var (pointed at the mongos routers for the sharded cluster).
    /// </summary>
    public static string? MonitorEnvVarName(TargetKey target) => target switch
    {
        TargetKey.MongoVm => "BMT_CONN_MONGO_MONITOR",
        TargetKey.MongoShard => "BMT_CONN_MONGO_MONITOR",
        _ => null,
    };

    /// <summary>Parse a CLI <c>--target</c> token into a <see cref="TargetKey"/>.</summary>
    public static TargetKey Parse(string cliName)
    {
        ArgumentNullException.ThrowIfNull(cliName);
        return cliName.Trim().ToLowerInvariant() switch
        {
            "cosmos-ru" => TargetKey.CosmosRu,
            "documentdb" => TargetKey.DocumentDb,
            "mongo-vm" => TargetKey.MongoVm,
            "mongo-shard" => TargetKey.MongoShard,
            _ => throw new ArgumentException(
                $"Unknown --target '{cliName}'. Expected one of: cosmos-ru, documentdb, mongo-vm, mongo-shard.",
                nameof(cliName)),
        };
    }

    /// <summary>
    /// Resolve the connection string for a target from its env var (Process scope first, then User
    /// scope on Windows). Throws if unset so we never silently run against the wrong endpoint.
    /// </summary>
    public static string ResolveConnectionString(TargetKey target)
    {
        var name = EnvVarName(target);
        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Connection string env var '{name}' for target '{CliName(target)}' is not set. " +
                "Set it at User scope on VM1 (see handoff §2) — do not place secrets in the repo.");
        }

        return value;
    }

    /// <summary>
    /// Resolve the optional <c>bmt_monitor</c> connection string for a target (Process scope first,
    /// then User scope on Windows). Returns <c>null</c> when the target has no monitor var or the var
    /// is unset — callers treat that as "monitor capture unavailable" rather than a hard failure.
    /// </summary>
    public static string? ResolveMonitorConnectionString(TargetKey target)
    {
        var name = MonitorEnvVarName(target);
        if (name is null)
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Cosmos RU does not support retryable writes (handoff §3); the factory forces
    /// <c>RetryWrites=false</c> for this target regardless of the connection-string flag.
    /// </summary>
    public static bool RequiresRetryWritesDisabled(TargetKey target) => target == TargetKey.CosmosRu;
}
