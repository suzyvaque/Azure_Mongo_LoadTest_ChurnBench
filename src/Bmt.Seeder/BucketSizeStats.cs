namespace Bmt.Seeder;

/// <summary>Tracks actual whole-document BSON sizes per bucket so the seeder can prove the sizing.</summary>
internal sealed class BucketSizeStats
{
    private sealed class Acc
    {
        public long Count;
        public int Target;
        public int Min = int.MaxValue;
        public int Max = int.MinValue;
        public long Sum;
        public long NonExact;
    }

    private readonly Dictionary<string, Acc> _byBucket = new();

    public void Record(string bucket, int targetBytes, int actualBytes)
    {
        if (!_byBucket.TryGetValue(bucket, out var a))
        {
            a = new Acc { Target = targetBytes };
            _byBucket[bucket] = a;
        }

        a.Count++;
        a.Sum += actualBytes;
        if (actualBytes < a.Min) a.Min = actualBytes;
        if (actualBytes > a.Max) a.Max = actualBytes;
        if (actualBytes != targetBytes) a.NonExact++;
    }

    public long TotalNonExact => _byBucket.Values.Sum(a => a.NonExact);

    public long TotalDocs => _byBucket.Values.Sum(a => a.Count);

    public long TotalBytes => _byBucket.Values.Sum(a => a.Sum);

    /// <summary>Render a human-readable size table for the seeding log.</summary>
    public void Log()
    {
        ConsoleLog.Info("Whole-document BSON size verification (actual vs. target):");
        ConsoleLog.Info($"  {"Bucket",-8} {"Target",10} {"Min",10} {"Max",10} {"Mean",10} {"Count",10} {"NonExact",10}");
        foreach (var (name, a) in _byBucket.OrderBy(kv => kv.Value.Target))
        {
            var mean = a.Count == 0 ? 0 : (double)a.Sum / a.Count;
            ConsoleLog.Info(
                $"  {name,-8} {a.Target,10:N0} {a.Min,10:N0} {a.Max,10:N0} {mean,10:N0} {a.Count,10:N0} {a.NonExact,10:N0}");
        }

        var totalMean = TotalDocs == 0 ? 0 : (double)TotalBytes / TotalDocs;
        ConsoleLog.Info($"  Total docs={TotalDocs:N0} mean={totalMean:N0} B (~{totalMean / 1024.0:N1} KB) " +
                        $"totalBytes={TotalBytes:N0} (~{TotalBytes / (1024.0 * 1024 * 1024):N2} GB)");

        if (TotalNonExact == 0)
        {
            ConsoleLog.Info("  All generated documents hit their whole-doc byte target EXACTLY.");
        }
        else
        {
            ConsoleLog.Warn($"  {TotalNonExact:N0} document(s) did not hit the exact byte target — investigate sizing.");
        }
    }
}
