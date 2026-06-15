namespace Bmt.Core.Metrics;

/// <summary>
/// Collects latency samples (in milliseconds) and computes the percentiles the comparison hinges on
/// (test_instruction.md §7: p50/p90/p95/p99/p99.9, prioritized over the average). Thread-safe via
/// striped locked buffers to keep contention low under the high op rate of the churn workload;
/// percentiles are computed once at the end by sorting the merged samples.
/// </summary>
public sealed class LatencyDigest
{
    private readonly object[] _locks;
    private readonly List<double>[] _shards;

    public LatencyDigest(int shardCount = 16)
    {
        if (shardCount <= 0)
        {
            shardCount = 1;
        }

        _locks = new object[shardCount];
        _shards = new List<double>[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            _locks[i] = new object();
            _shards[i] = new List<double>();
        }
    }

    /// <summary>Record one latency sample in milliseconds.</summary>
    public void Record(double milliseconds)
    {
        var shard = (Environment.CurrentManagedThreadId & int.MaxValue) % _shards.Length;
        lock (_locks[shard])
        {
            _shards[shard].Add(milliseconds);
        }
    }

    /// <summary>Merge all shards into one sorted ascending array (call once after the run completes).</summary>
    public double[] SortedSamples()
    {
        var total = 0;
        for (var i = 0; i < _shards.Length; i++)
        {
            lock (_locks[i])
            {
                total += _shards[i].Count;
            }
        }

        var merged = new double[total];
        var offset = 0;
        for (var i = 0; i < _shards.Length; i++)
        {
            lock (_locks[i])
            {
                _shards[i].CopyTo(merged, offset);
                offset += _shards[i].Count;
            }
        }

        Array.Sort(merged);
        return merged;
    }

    /// <summary>Compute the §7 latency summary from this digest.</summary>
    public LatencySummary Summarize() => LatencySummary.FromSorted(SortedSamples());
}

/// <summary>The percentile summary for one latency series (all values in milliseconds).</summary>
public sealed class LatencySummary
{
    public long Count { get; set; }

    public double MinMs { get; set; }

    public double MaxMs { get; set; }

    public double MeanMs { get; set; }

    public double P50Ms { get; set; }

    public double P90Ms { get; set; }

    public double P95Ms { get; set; }

    public double P99Ms { get; set; }

    public double P999Ms { get; set; }

    public static LatencySummary Empty() => new();

    /// <summary>Build a summary from an already-sorted ascending sample array.</summary>
    public static LatencySummary FromSorted(double[] sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Length == 0)
        {
            return new LatencySummary();
        }

        double sum = 0;
        foreach (var v in sorted)
        {
            sum += v;
        }

        return new LatencySummary
        {
            Count = sorted.Length,
            MinMs = sorted[0],
            MaxMs = sorted[^1],
            MeanMs = sum / sorted.Length,
            P50Ms = Percentile(sorted, 0.50),
            P90Ms = Percentile(sorted, 0.90),
            P95Ms = Percentile(sorted, 0.95),
            P99Ms = Percentile(sorted, 0.99),
            P999Ms = Percentile(sorted, 0.999),
        };
    }

    /// <summary>Nearest-rank percentile on an ascending-sorted array.</summary>
    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(q * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
