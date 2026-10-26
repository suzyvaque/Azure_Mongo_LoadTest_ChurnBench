using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Connections;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using Xunit;

namespace Bmt.Tests;

/// <summary>
/// Loads every shipped config through <see cref="BmtConfig.Load"/> (JSONC comments + Extends chains) and
/// runs its validation, so a stray comment/trailing-comma or an invalid production value fails CI rather
/// than the operator's live run. Also pins the invariants the benchmark-correctness work depends on
/// (the 300 s arrival window and 3 production iterations).
/// </summary>
public sealed class ConfigLoadTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Bmt.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not locate repo root (Bmt.sln).");
    }

    public static IEnumerable<object[]> AllConfigs()
    {
        var configDir = Path.Combine(RepoRoot(), "config");
        foreach (var path in Directory.EnumerateFiles(configDir, "*.json", SearchOption.AllDirectories))
        {
            // azure-resources.json is a resource catalog, not a BmtConfig.
            if (Path.GetFileName(path).Equals("azure-resources.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new object[] { path };
        }
    }

    [Theory]
    [MemberData(nameof(AllConfigs))]
    public void EveryConfig_Loads_And_Validates(string path)
    {
        var config = BmtConfig.Load(path);
        config.Validate();
        Assert.True(config.Scenario.Iterations > 0);
        Assert.True(config.Scenario.IterationDurationSeconds >= 0);
    }

    [Fact]
    public void ProductionRun_Uses_300s_ArrivalWindow_And_3_Iterations()
    {
        var path = Path.Combine(RepoRoot(), "config", "production", "run.json");
        var config = BmtConfig.Load(path);

        Assert.Equal(3, config.Scenario.Iterations);
        Assert.Equal(300, config.Scenario.IterationDurationSeconds);
    }

    [Fact]
    public void DocumentDbHaProfile_PreservesSrv_And_AppliesResilienceSettings()
    {
        var path = Path.Combine(
            RepoRoot(), "config", "production", "documentdb-ha-full-workload.json");
        var config = BmtConfig.Load(path);
        var factory = new TaskConnectionFactory(
            TargetKey.DocumentDb,
            "mongodb+srv://user:password@example.test/?tls=true&retryWrites=false&retryReads=false",
            tuning: config.Client);

        var settings = factory.BuildSettings();

        Assert.Equal(ConnectionStringScheme.MongoDBPlusSrv, settings.Scheme);
        Assert.True(settings.RetryWrites);
        Assert.True(settings.RetryReads);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.ServerSelectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.SocketTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.MaxConnectionIdleTime);
        Assert.Equal(ReadPreference.PrimaryPreferred, settings.ReadPreference);
        Assert.Equal(ReadConcern.Majority, settings.ReadConcern);
        Assert.Equal(WriteConcern.WMajority, settings.WriteConcern);
        Assert.NotEqual(true, settings.DirectConnection);
        Assert.Equal(1, settings.MaxConnectionPoolSize);
        Assert.Equal(0, settings.MinConnectionPoolSize);
    }

    [Fact]
    public void DocumentDbHaProfile_RejectsPinnedNonSrvEndpoint()
    {
        var config = new ClientConfig { PreserveDocumentDbSrvEndpoint = true };
        var factory = new TaskConnectionFactory(
            TargetKey.DocumentDb,
            "mongodb://user:password@example.test:10260/?tls=true",
            tuning: config);

        var error = Assert.Throws<InvalidOperationException>(() => factory.BuildSettings());
        Assert.Contains("mongodb+srv", error.Message);
    }
}
