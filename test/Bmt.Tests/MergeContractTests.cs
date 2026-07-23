using Bmt.Core.Metrics;
using Bmt.Report;
using Xunit;

namespace Bmt.Tests;

/// <summary>
/// Validates the §1 merge contract: group per synchronized iteration, require the exact host set,
/// dedupe retries to the latest attempt per host, report start-time skew, and exclude invalid /
/// entirely-missing iterations from the cross-iteration summary.
/// </summary>
public sealed class MergeContractTests : IDisposable
{
    private readonly string _dir;

    public MergeContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bmt-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteRun(int hostId, int hostCount, int iter, int iterCount, long startedUnix,
        long connPeak, int inflight, string name, string tag = "camp")
    {
        var r = new RunResult
        {
            Target = "documentdb",
            Scenario = "Burst",
            HostId = hostId,
            HostCount = hostCount,
            RunTag = tag,
            StartedUnixSeconds = startedUnix,
            WorkloadMode = "full-workload",
            IterationNumber = iter,
            IterationCount = iterCount,
            Totals = new TaskTotals { TotalTasks = 100, SuccessfulTasks = 100 },
            Throughput = new List<ThroughputPoint>
            {
                new() { Second = 1, ConnectionsCreated = connPeak, InFlightTasks = inflight },
            },
        };
        File.WriteAllText(Path.Combine(_dir, name + ".json"), r.ToJson());
    }

    [Fact]
    public void RequiresExactHostSet_And_ReportsSkew()
    {
        // iteration 1: all 3 hosts with small skew (0,1,2s) -> valid, skew 2.
        WriteRun(1, 3, 1, 2, 1000, 500, 4000, "i1h1");
        WriteRun(2, 3, 1, 2, 1001, 500, 4000, "i1h2");
        WriteRun(3, 3, 1, 2, 1002, 500, 4000, "i1h3");
        // iteration 2: host 3 missing -> invalid.
        WriteRun(1, 3, 2, 2, 2000, 450, 3900, "i2h1");
        WriteRun(2, 3, 2, 2, 2000, 450, 3900, "i2h2");

        var report = Merger.Merge(_dir, "camp", 11000, 1200);

        var i1 = report.Groups.Single(g => g.IterationNumber == 1);
        Assert.True(i1.Valid);
        Assert.Equal(new[] { 1, 2, 3 }, i1.HostIds.ToArray());
        Assert.Equal(2, i1.StartSkewSeconds);

        var i2 = report.Groups.Single(g => g.IterationNumber == 2);
        Assert.False(i2.Valid);
        Assert.Equal(new[] { 3 }, i2.MissingHostIds.ToArray());

        var stat = report.CrossIteration.Stats.Single();
        Assert.Equal(1, stat.ValidIterations);
        Assert.Contains(2, stat.InvalidIterationNumbers);
    }

    [Fact]
    public void Retries_AreDeduped_ToLatestAttemptPerHost()
    {
        // iteration 1 attempt 1: hosts 1,2 succeeded (t=1000) but host 3 failed -> no host-3 artifact.
        WriteRun(1, 3, 1, 1, 1000, 400, 3000, "a1h1");
        WriteRun(2, 3, 1, 1, 1000, 400, 3000, "a1h2");
        // iteration 1 attempt 2 (retry): all 3 succeed at t=1400 with higher peaks.
        WriteRun(1, 3, 1, 1, 1400, 500, 4000, "a2h1");
        WriteRun(2, 3, 1, 1, 1400, 500, 4000, "a2h2");
        WriteRun(3, 3, 1, 1, 1400, 500, 4000, "a2h3");

        var report = Merger.Merge(_dir, "camp", 11000, 1200);
        var g = report.Groups.Single();

        Assert.True(g.Valid);
        Assert.Equal(new[] { 1, 2, 3 }, g.HostIds.ToArray());
        Assert.Equal(0, g.StartSkewSeconds);              // latest attempt only -> all at 1400
        Assert.Equal(2, g.SupersededRuns);                 // two stale attempt-1 artifacts dropped
        Assert.Equal(1500, g.PeakCombinedConnPerSec);      // 3 x 500 from the retry, not the stale 400s
    }

    [Fact]
    public void EntirelyMissingIteration_IsSurfaced()
    {
        // Only iteration 1 present; hosts declare IterationCount=3, so iters 2 and 3 are missing.
        WriteRun(1, 3, 1, 3, 1000, 500, 4000, "h1");
        WriteRun(2, 3, 1, 3, 1000, 500, 4000, "h2");
        WriteRun(3, 3, 1, 3, 1000, 500, 4000, "h3");

        var report = Merger.Merge(_dir, "camp", 11000, 1200);
        var stat = report.CrossIteration.Stats.Single();

        Assert.Equal(3, stat.ExpectedIterations);
        Assert.Equal(1, stat.ValidIterations);
        Assert.Equal(new[] { 2, 3 }, stat.MissingIterationNumbers.ToArray());
    }

    [Fact]
    public void UnexpectedHostId_InvalidatesIteration()
    {
        WriteRun(1, 3, 1, 1, 1000, 500, 4000, "h1");
        WriteRun(2, 3, 1, 1, 1000, 500, 4000, "h2");
        WriteRun(3, 3, 1, 1, 1000, 500, 4000, "h3");
        WriteRun(4, 3, 1, 1, 1000, 500, 4000, "h4"); // host 4 outside required 1..3

        var report = Merger.Merge(_dir, "camp", 11000, 1200);
        var g = report.Groups.Single();
        Assert.False(g.Valid);
        Assert.Contains(4, g.UnexpectedHostIds);
    }
}
