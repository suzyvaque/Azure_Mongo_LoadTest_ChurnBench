using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bmt.Report;

/// <summary>
/// Lightweight projection of a preflight JSON artifact (written by the <c>preflight</c> command). Only
/// the fields the HTML report needs are modeled, so Bmt.Report does not depend on the Bmt.Preflight
/// assembly. Used to enrich each target's config summary (§6.3 check 6) and to surface the unique-vs-
/// non-unique ReqId-index divergence + distinct-ReqId guarantee (cosmos-ru).
/// </summary>
public sealed class PreflightSummary
{
    public string Target { get; set; } = string.Empty;

    public string TimestampUtc { get; set; } = string.Empty;

    public long InputDocumentCount { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public PfIndexPolicy IndexPolicy { get; set; } = new();

    public PfServerConfig ServerConfig { get; set; } = new();

    public PfHostHeadroom HostHeadroom { get; set; } = new();

    public List<PfCheck> Checks { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>True if this JSON document is a preflight artifact (has a Checks array + IndexPolicy).</summary>
    public static bool LooksLikePreflight(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Checks", out _) &&
        root.TryGetProperty("IndexPolicy", out _);

    public static PreflightSummary FromJson(string json) =>
        JsonSerializer.Deserialize<PreflightSummary>(json, Options)
        ?? throw new InvalidOperationException("Preflight JSON deserialized to null.");
}

public sealed class PfIndexPolicy
{
    public bool InputIndexPresent { get; set; }

    public bool InputIndexUnique { get; set; }

    public bool OutputIndexPresent { get; set; }

    public bool UniquenessDivergesFromCanonical { get; set; }

    public string DistinctReqIdGuarantee { get; set; } = string.Empty;
}

public sealed class PfServerConfig
{
    public string? ServerVersion { get; set; }

    public int? CosmosExpectedRuPerSec { get; set; }

    public string? DocumentDbExpectedTier { get; set; }

    public int? MongoExpectedMaxIncomingConnections { get; set; }

    public int? MongoLiveConnectionCeiling { get; set; }
}

public sealed class PfHostHeadroom
{
    public int EphemeralPortStart { get; set; }

    public int EphemeralPortCount { get; set; }

    public int TcpTimedWaitDelaySeconds { get; set; }

    public bool TcpTimedWaitDelayIsDefault { get; set; }

    public double ChurnCapacityPerSec { get; set; }
}

public sealed class PfCheck
{
    public int Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}
