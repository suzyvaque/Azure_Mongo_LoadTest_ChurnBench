using System.Text.Json;
using System.Text.Json.Nodes;
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

    public WorkloadConfig Workload { get; set; } = new();

    /// <summary>Per-Task calc-time substitute sleep (test_instruction.md §6.6). Default 10,000 ms.</summary>
    public int TaskSleepMs { get; set; } = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load and validate a config file from disk. A config may inherit a shared base file via a
    /// top-level <c>"Extends": "&lt;path&gt;"</c> field (path relative to the including file, or absolute).
    /// The base is loaded first and the including file is deep-merged over it (child wins; nested
    /// objects merge key-by-key, scalars/arrays are replaced). <c>Extends</c> chains are followed
    /// recursively, so a common production file can hold the scenario/dataset/preflight envelope while
    /// each per-test config only overrides what differs.
    /// </summary>
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

        var merged = LoadMergedObject(Path.GetFullPath(path), depth: 0);
        var config = merged.Deserialize<BmtConfig>(JsonOptions)
                     ?? throw new InvalidOperationException($"Config file '{path}' deserialized to null.");
        config.Validate();
        return config;
    }

    /// <summary>Parse one config file, resolve its <c>Extends</c> base (if any), and return the merged object.</summary>
    private static JsonObject LoadMergedObject(string path, int depth)
    {
        if (depth > 10)
        {
            throw new InvalidOperationException(
                $"Config 'Extends' chain too deep (possible cycle) while loading '{path}'.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found (referenced via Extends): {path}", path);
        }

        var json = File.ReadAllText(path);
        if (JsonNode.Parse(json, nodeOptions: null, documentOptions: DocOptions) is not JsonObject node)
        {
            throw new InvalidOperationException($"Config file '{path}' is not a JSON object.");
        }

        var extends = TakeExtends(node);
        if (extends is null)
        {
            return node;
        }

        var basePath = Path.IsPathRooted(extends)
            ? extends
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, extends));
        var baseObj = LoadMergedObject(basePath, depth + 1);
        DeepMerge(baseObj, node);
        return baseObj;
    }

    /// <summary>Remove and return the (case-insensitive) <c>Extends</c> value from a config object.</summary>
    private static string? TakeExtends(JsonObject obj)
    {
        string? key = null;
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, "Extends", StringComparison.OrdinalIgnoreCase))
            {
                key = kv.Key;
                break;
            }
        }

        if (key is null)
        {
            return null;
        }

        var value = obj[key]?.GetValue<string>();
        obj.Remove(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Recursively merge <paramref name="overlay"/> into <paramref name="baseObj"/> (overlay wins).</summary>
    private static void DeepMerge(JsonObject baseObj, JsonObject overlay)
    {
        foreach (var kv in overlay)
        {
            if (baseObj.TryGetPropertyValue(kv.Key, out var baseVal)
                && baseVal is JsonObject baseChild
                && kv.Value is JsonObject overlayChild)
            {
                DeepMerge(baseChild, overlayChild);
            }
            else
            {
                baseObj[kv.Key] = kv.Value?.DeepClone();
            }
        }
    }

    public void Validate()
    {
        Dataset.Validate();
        Seeder.Validate();
        Preflight.Validate();
        Scenario.Validate();
        Client.Validate();
        Workload.Validate();
        if (TaskSleepMs < 0)
        {
            throw new InvalidOperationException("TaskSleepMs must be >= 0.");
        }
    }
}
