using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bmt.Core.Configuration;

/// <summary>
/// Root configuration for the benchmark (the <c>--config config.json</c> file). Phase-1 v1 wires the
/// dataset + seeder sections; scenario/run sections are added by the load generator milestone.
/// </summary>
public sealed class BmtConfig
{
    public DatasetConfig Dataset { get; set; } = DatasetConfig.Default();

    public SeederConfig Seeder { get; set; } = new();

    public PreflightConfig Preflight { get; set; } = new();

    public ScenarioConfig Scenario { get; set; } = new();

    public ClientConfig Client { get; set; } = new();

    /// <summary>Per-Task calc-time substitute sleep (test_instruction.md §6.6). Default 10,000 ms.</summary>
    public int TaskSleepMs { get; set; } = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Load and validate a config file from disk.</summary>
    public static BmtConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Config path must be provided.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<BmtConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException($"Config file '{path}' deserialized to null.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        Dataset.Validate();
        Seeder.Validate();
        Preflight.Validate();
        Scenario.Validate();
        Client.Validate();
        if (TaskSleepMs < 0)
        {
            throw new InvalidOperationException("TaskSleepMs must be >= 0.");
        }
    }
}
