using System.Reflection;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;

namespace Bmt.Core.Connections;

/// <summary>
/// Releases a per-Task <see cref="MongoClient"/>'s underlying cluster and connection.
///
/// The official driver 2.x does not implement <c>IDisposable</c> on <see cref="MongoClient"/>;
/// clusters are cached in <c>MongoDB.Driver.ClusterRegistry</c> keyed by settings. To honor the
/// no-reuse requirement (test_instruction.md §2.3 — "connection resources actually released after
/// each request") and to avoid leaking registry entries across the hundreds of thousands of Tasks
/// in a run, this helper resolves the client's public <c>Cluster</c> and calls
/// <c>ClusterRegistry.Instance.UnregisterAndDisposeCluster(cluster)</c> (which both removes the
/// registry entry and disposes the cluster). If that internal API ever changes, it falls back to
/// disposing the cluster directly.
///
/// Because <see cref="TaskConnectionFactory"/> gives every client a distinct
/// <c>ClusterConfigurator</c>, each Task gets its own cluster — so unregistering one never affects
/// another Task's connection.
/// </summary>
internal static class MongoClientReleaser
{
    private static readonly object? RegistryInstance;
    private static readonly MethodInfo? UnregisterMethod;

    static MongoClientReleaser()
    {
        // ClusterRegistry lives in MongoDB.Driver.dll (same assembly as MongoClient).
        var registryType = typeof(MongoClient).Assembly.GetType("MongoDB.Driver.ClusterRegistry");

        RegistryInstance = registryType
            ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);

        UnregisterMethod = registryType?.GetMethod(
            "UnregisterAndDisposeCluster",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(ICluster) },
            modifiers: null);
    }

    public static void Release(MongoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var cluster = client.Cluster;
        if (cluster is null)
        {
            return;
        }

        if (RegistryInstance is not null && UnregisterMethod is not null)
        {
            try
            {
                UnregisterMethod.Invoke(RegistryInstance, new object[] { cluster });
                return;
            }
            catch
            {
                // Fall through to direct disposal if the internal API shape changed.
            }
        }

        (cluster as IDisposable)?.Dispose();
    }
}
