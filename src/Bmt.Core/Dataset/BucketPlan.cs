using Bmt.Core.Configuration;

namespace Bmt.Core.Dataset;

/// <summary>
/// Deterministic mapping of each document's 1-based id to a size bucket. The assignment is a
/// Fisher–Yates shuffle seeded by <see cref="DatasetConfig.Seed"/>, so:
/// <list type="bullet">
///   <item>the exact per-bucket counts (10k/15k/35k/40k) are honored,</item>
///   <item>sizes are interspersed across the id range (a realistic working set, not clustered), and</item>
///   <item>the layout is byte-identical across all three targets and fully reproducible on resume.</item>
/// </list>
/// </summary>
public sealed class BucketPlan
{
    private readonly BucketConfig[] _bucketForId; // index 0 == document id 1

    private BucketPlan(BucketConfig[] bucketForId) => _bucketForId = bucketForId;

    public int DocumentCount => _bucketForId.Length;

    /// <summary>Bucket assigned to a 1-based document id.</summary>
    public BucketConfig BucketForId(int id)
    {
        if (id < 1 || id > _bucketForId.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"id must be in [1, {_bucketForId.Length}].");
        }

        return _bucketForId[id - 1];
    }

    public static BucketPlan Build(DatasetConfig dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        dataset.Validate();

        var array = new BucketConfig[dataset.DocumentCount];
        var i = 0;
        foreach (var bucket in dataset.Buckets)
        {
            for (var k = 0; k < bucket.Count; k++)
            {
                array[i++] = bucket;
            }
        }

        // Deterministic Fisher–Yates shuffle (seed-driven) so the mix is reproducible & identical
        // across backends.
        var rng = new Random(dataset.Seed);
        for (var n = array.Length - 1; n > 0; n--)
        {
            var j = rng.Next(n + 1);
            (array[n], array[j]) = (array[j], array[n]);
        }

        return new BucketPlan(array);
    }
}
