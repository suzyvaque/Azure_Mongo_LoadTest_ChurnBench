using System.Text.Json;
using System.Text.Json.Serialization;
using Bmt.Core;
using Bmt.Core.Errors;

namespace Bmt.Preflight;

/// <summary>Outcome of a single preflight check.</summary>
public enum PreflightStatus
{
    /// <summary>The precondition is satisfied.</summary>
    Pass,

    /// <summary>Non-blocking concern — the run may proceed but the operator should be aware.</summary>
    Warn,

    /// <summary>Blocking failure — the timed run MUST NOT start (results would be invalid).</summary>
    Fail,
}

/// <summary>Result of one of the ten §6.3 preflight checks.</summary>
public sealed class PreflightCheckResult
{
    public PreflightCheckResult(int number, string name, PreflightStatus status, string detail, BmtErrorType? errorType = null)
    {
        Number = number;
        Name = name;
        Status = status;
        Detail = detail;
        ErrorType = errorType;
    }

    /// <summary>1-based check number matching test_instruction.md §6.3.</summary>
    public int Number { get; }

    public string Name { get; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PreflightStatus Status { get; }

    public string Detail { get; }

    /// <summary>The §7.4 error bucket for a failure (e.g. DataSetMissing / IndexMissing).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BmtErrorType? ErrorType { get; }

    public static PreflightCheckResult Pass(int n, string name, string detail) =>
        new(n, name, PreflightStatus.Pass, detail);

    public static PreflightCheckResult Warn(int n, string name, string detail) =>
        new(n, name, PreflightStatus.Warn, detail);

    public static PreflightCheckResult Fail(int n, string name, string detail, BmtErrorType errorType) =>
        new(n, name, PreflightStatus.Fail, detail, errorType);
}

/// <summary>
/// Per-collection ReqId index policy, recorded so the unique-vs-non-unique divergence (cosmos-ru is
/// non-unique by platform constraint) is explicit in BOTH the preflight output and the HTML report
/// (user requirement: record the divergence). <see cref="DistinctReqIdGuarantee"/> states how the
/// distinct-ReqId invariant is upheld even where the index is not unique.
/// </summary>
public sealed class IndexPolicy
{
    public bool InputIndexPresent { get; set; }

    public bool InputIndexUnique { get; set; }

    public bool OutputIndexPresent { get; set; }

    /// <summary>True when this target's input index uniqueness differs from the canonical unique policy.</summary>
    public bool UniquenessDivergesFromCanonical { get; set; }

    /// <summary>Human-readable explanation of how distinct ReqId is guaranteed on this target.</summary>
    public string DistinctReqIdGuarantee { get; set; } = string.Empty;
}

/// <summary>Expected/observed server configuration recorded for the report config summary (§6.3 check 6).</summary>
public sealed class ServerConfigSummary
{
    public string? ServerVersion { get; set; }

    public int? CosmosExpectedRuPerSec { get; set; }

    public string? DocumentDbExpectedTier { get; set; }

    public int? MongoExpectedMaxIncomingConnections { get; set; }

    public int? MongoLiveConnectionCeiling { get; set; }
}

/// <summary>Client-host TCP/headroom facts recorded for the report (§7.3).</summary>
public sealed class HostHeadromSummary
{
    public int EphemeralPortStart { get; set; }

    public int EphemeralPortCount { get; set; }

    public int TcpTimedWaitDelaySeconds { get; set; }

    public bool TcpTimedWaitDelayIsDefault { get; set; }

    /// <summary>Connections that can be churned per second = ports / TIME_WAIT delay.</summary>
    public double ChurnCapacityPerSec { get; set; }
}

/// <summary>The full preflight result for one target — serialized to JSON for the report module.</summary>
public sealed class PreflightReport
{
    public string Target { get; set; } = string.Empty;

    public string TimestampUtc { get; set; } = string.Empty;

    public string MaskedConnectionString { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = BmtConstants.DatabaseName;

    public long InputDocumentCount { get; set; }

    public int RequiredDocumentCount { get; set; } = BmtConstants.RequiredInputDocCount;

    public IndexPolicy IndexPolicy { get; set; } = new();

    public ServerConfigSummary ServerConfig { get; set; } = new();

    public HostHeadromSummary HostHeadroom { get; set; } = new();

    public List<PreflightCheckResult> Checks { get; set; } = new();

    /// <summary>Overall gate: Pass (all pass), Warn (≥1 warn, 0 fail), or Fail (≥1 fail → abort).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PreflightStatus Outcome =>
        Checks.Any(c => c.Status == PreflightStatus.Fail) ? PreflightStatus.Fail
        : Checks.Any(c => c.Status == PreflightStatus.Warn) ? PreflightStatus.Warn
        : PreflightStatus.Pass;

    /// <summary>True if no check failed — the timed run is allowed to start.</summary>
    public bool CanProceed => Outcome != PreflightStatus.Fail;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
