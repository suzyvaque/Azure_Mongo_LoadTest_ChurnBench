using Bmt.Core.Configuration;
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
}
