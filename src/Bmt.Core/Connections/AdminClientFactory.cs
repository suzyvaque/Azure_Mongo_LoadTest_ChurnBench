using MongoDB.Driver;

namespace Bmt.Core.Connections;

/// <summary>
/// Builds a normal, pooled <see cref="MongoClient"/> for bulk/admin operations (seeding,
/// index creation, preflight setup). This is intentionally NOT the no-reuse per-Task client:
/// the §2.2/§2.3 no-reuse rules govern the measured Task workload only. For Cosmos RU this still
/// forces <c>RetryWrites=false</c> (handoff §3); throttling is handled by caller backoff, and the
/// provisioned RU/s is never changed.
/// </summary>
public sealed class AdminClientFactory
{
    public static MongoClient Create(TargetKey target, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));
        }

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        if (TargetConnection.RequiresRetryWritesDisabled(target))
        {
            settings.RetryWrites = false;
        }

        return new MongoClient(settings);
    }

    /// <summary>Build an admin client resolving the connection string from the target's env var.</summary>
    public static MongoClient FromEnvironment(TargetKey target) =>
        Create(target, TargetConnection.ResolveConnectionString(target));
}
