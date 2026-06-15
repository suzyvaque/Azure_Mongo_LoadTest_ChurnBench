namespace Bmt.Core.Configuration;

/// <summary>
/// Dataset definition (test_instruction.md §3). Defaults encode the authoritative spec exactly:
/// 100,000 docs, seed 42, four whole-document size buckets 6/16/50/58 KB at 10k/15k/35k/40k.
/// </summary>
public sealed class DatasetConfig
{
    public int DocumentCount { get; set; } = BmtConstants.RequiredInputDocCount;

    public int Seed { get; set; } = BmtConstants.DatasetSeed;

    public List<BucketConfig> Buckets { get; set; } = new();

    /// <summary>The authoritative default dataset spec.</summary>
    public static DatasetConfig Default() => new()
    {
        DocumentCount = BmtConstants.RequiredInputDocCount,
        Seed = BmtConstants.DatasetSeed,
        Buckets = new List<BucketConfig>
        {
            new() { Name = "Small",  SizeBytes = 6 * 1024,  Count = 10_000 },
            new() { Name = "Medium", SizeBytes = 16 * 1024, Count = 15_000 },
            new() { Name = "Large",  SizeBytes = 50 * 1024, Count = 35_000 },
            new() { Name = "XL",     SizeBytes = 58 * 1024, Count = 40_000 },
        },
    };

    public void Validate()
    {
        if (DocumentCount <= 0)
        {
            throw new InvalidOperationException("Dataset.DocumentCount must be > 0.");
        }

        if (Buckets is null || Buckets.Count == 0)
        {
            throw new InvalidOperationException("Dataset.Buckets must contain at least one bucket.");
        }

        foreach (var b in Buckets)
        {
            b.Validate();
        }

        var sum = Buckets.Sum(b => b.Count);
        if (sum != DocumentCount)
        {
            throw new InvalidOperationException(
                $"Dataset bucket counts sum to {sum:N0} but DocumentCount is {DocumentCount:N0} — they must match exactly.");
        }
    }

    /// <summary>Weighted mean whole-document size in bytes (for logging / report config summary).</summary>
    public double MeanBytes() =>
        DocumentCount == 0 ? 0 : Buckets.Sum(b => (double)b.SizeBytes * b.Count) / DocumentCount;
}

/// <summary>A single whole-document size bucket: <see cref="Count"/> docs of <see cref="SizeBytes"/> each.</summary>
public sealed class BucketConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Target WHOLE-document BSON size in bytes (Input is padded to hit this exactly).</summary>
    public int SizeBytes { get; set; }

    public int Count { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Bucket.Name must be set.");
        }

        if (SizeBytes <= 0)
        {
            throw new InvalidOperationException($"Bucket '{Name}': SizeBytes must be > 0.");
        }

        if (Count <= 0)
        {
            throw new InvalidOperationException($"Bucket '{Name}': Count must be > 0.");
        }
    }
}
