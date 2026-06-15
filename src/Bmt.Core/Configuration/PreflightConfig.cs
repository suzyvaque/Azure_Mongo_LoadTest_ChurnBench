namespace Bmt.Core.Configuration;

/// <summary>
/// Settings for the preflight gate (test_instruction.md §6.3). All values are optional with sane
/// defaults so an existing <c>config.json</c> keeps loading; thresholds drive the warn/fail verdicts
/// of the host-headroom (§7.3), clean-state, and warm-up checks. Expected server values (§6.3 check 6)
/// are recorded verbatim in the preflight output and HTML report config summary.
/// </summary>
public sealed class PreflightConfig
{
    /// <summary>Directory for run artifacts (preflight JSON, raw run output). Created if absent.</summary>
    public string ResultsDirectory { get; set; } = "artifacts";

    /// <summary>Minimum free disk required in the results drive before a run (check 9). Default 1 GiB.</summary>
    public long MinFreeDiskBytes { get; set; } = 1L << 30;

    /// <summary>Concurrent-connection target the host must be able to sustain (§6.2 burst ≥ 11,000).</summary>
    public int ConcurrentConnectionTarget { get; set; } = 11_000;

    /// <summary>Instantaneous connection-open rate target, conn/sec (§6.2 burst ≥ 1,200).</summary>
    public int ConnectionChurnPerSecTarget { get; set; } = 1_200;

    /// <summary>
    /// If true (default), a public-IP endpoint fails check 3 (private-path requirement). Set false to
    /// downgrade to a warning for local/dev runs that intentionally cross a non-private path.
    /// </summary>
    public bool RequirePrivateNetwork { get; set; } = true;

    /// <summary>Sample size for the queryability spot-check (check 1) and warm-up sweep (check 10).</summary>
    public int SampleSize { get; set; } = 1_000;

    /// <summary>Max age of the warm-up sentinel before check 10 warns it is stale. Default 6 h.</summary>
    public int WarmupMaxAgeMinutes { get; set; } = 360;

    /// <summary>Expected provisioned Cosmos RU/s (§4 fixed 40,000; check 6 records, never changes it).</summary>
    public int CosmosExpectedRuPerSec { get; set; } = 40_000;

    /// <summary>Expected DocumentDB (vCore) tier (§6.3 check 6). Recorded in the config summary.</summary>
    public string DocumentDbExpectedTier { get; set; } = "M80";

    /// <summary>
    /// Expected <c>mongod</c> <c>maxIncomingConnections</c> for mongo-vm (check 6). When set, the live
    /// connection ceiling is compared against it. Null = record live value only, no comparison.
    /// </summary>
    public int? MongoExpectedMaxIncomingConnections { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ResultsDirectory))
        {
            throw new InvalidOperationException("Preflight.ResultsDirectory must not be empty.");
        }

        if (MinFreeDiskBytes < 0 ||
            ConcurrentConnectionTarget <= 0 ||
            ConnectionChurnPerSecTarget <= 0 ||
            SampleSize <= 0 ||
            WarmupMaxAgeMinutes <= 0 ||
            CosmosExpectedRuPerSec <= 0)
        {
            throw new InvalidOperationException("Preflight numeric settings must be positive (MinFreeDiskBytes >= 0).");
        }
    }
}
