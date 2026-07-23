using Bmt.Core.Connections;
using Bmt.Core.Metrics;
using Bmt.Report;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using Xunit;

namespace Bmt.Tests;

/// <summary>
/// Validates the §3 connection-lifecycle model: driver-event-sourced counters + gauges with correct
/// state transitions (Connecting↔Ready↔Closed), peak retention, WaitingForServer, and the merge-level
/// ready/s + active-ready threshold evaluation. Driver events — not Task counts — are the authoritative
/// connection evidence.
/// </summary>
public sealed class LifecycleTests
{
    private static ConnectionId Conn(int client, long local)
    {
        var cluster = new ClusterId(client);
        var server = new ServerId(cluster, new DnsEndPoint("h" + client, 27017));
        return new ConnectionId(server, local);
    }

    [Fact]
    public void Gauges_Track_State_Transitions_And_Retain_Peaks()
    {
        var c = new ConnectionEventCounters();

        // WaitingForServer gauge: 3 concurrent selections, then all resolve.
        c.OnServerSelectionStarted();
        c.OnServerSelectionStarted();
        c.OnServerSelectionStarted();
        Assert.Equal(3, c.WaitingForServer);
        c.OnServerSelectionEnded(true);
        c.OnServerSelectionEnded(true);
        c.OnServerSelectionEnded(true);
        Assert.Equal(0, c.WaitingForServer);
        Assert.Equal(3, c.PeakWaitingForServer);

        var a = Conn(1, 1);
        var b = Conn(2, 1); // same local value, different client -> distinct key

        c.OnConnectionCreated(a);
        c.OnConnectionCreated(b);
        Assert.Equal(2, c.Created);
        Assert.Equal(2, c.ActiveConnecting);

        c.OnConnectionReady(a, TimeSpan.FromMilliseconds(5));
        c.OnConnectionReady(b, TimeSpan.FromMilliseconds(7));
        Assert.Equal(2, c.Ready);
        Assert.Equal(0, c.ActiveConnecting);
        Assert.Equal(2, c.ActiveReady);
        Assert.Equal(2, c.PeakActiveConnecting);
        Assert.Equal(2, c.PeakActiveReady);

        // Ready -> Closing -> Closed: connection leaves ActiveReady when it starts closing, not only at close.
        c.OnConnectionClosing(a);
        Assert.Equal(1, c.ActiveReady);
        Assert.Equal(1, c.ActiveClosing);
        c.OnConnectionClosed(a);
        Assert.Equal(0, c.ActiveClosing);
        Assert.Equal(1, c.PeakActiveClosing);

        c.OnConnectionClosed(b); // close without an explicit Closing event still decrements ActiveReady
        Assert.Equal(2, c.Closed);
        Assert.Equal(0, c.ActiveReady);      // fully drained
        Assert.Equal(2, c.PeakActiveReady);   // peak retained

        // A connection that fails while still Connecting must decrement ActiveConnecting, not ActiveReady.
        var d = Conn(3, 1);
        c.OnConnectionCreated(d);
        Assert.Equal(1, c.ActiveConnecting);
        c.OnConnectionFailed(d, new Exception("boom"));
        Assert.Equal(1, c.Failed);
        Assert.Equal(0, c.ActiveConnecting);
        Assert.Equal(0, c.ActiveReady);
    }

    [Fact]
    public void Failure_Accounting_Is_Idempotent_Across_Duplicate_Events()
    {
        var c = new ConnectionEventCounters();
        var a = Conn(1, 1);
        c.OnConnectionCreated(a);

        // The driver may emit BOTH ConnectionOpeningFailedEvent and ConnectionFailedEvent for one failed
        // open; only the first is counted (the per-connection state entry is removed on the first).
        c.OnConnectionFailed(a, new Exception("open failed"));
        c.OnConnectionFailed(a, new Exception("duplicate"));
        Assert.Equal(1, c.Failed);
        Assert.Equal(0, c.ActiveConnecting);
    }

    [Fact]
    public void Merge_Evaluates_ReadyPerSec_And_ActiveReady_Thresholds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmt-lc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 3 hosts aligned at the same second, each 500 created/ready per sec and 4000 active-ready.
            foreach (var hostId in new[] { 1, 2, 3 })
            {
                var r = new RunResult
                {
                    Target = "documentdb",
                    Scenario = "Burst",
                    HostId = hostId,
                    HostCount = 3,
                    RunTag = "camp",
                    StartedUnixSeconds = 1000,
                    IterationNumber = 1,
                    IterationCount = 1,
                    Totals = new TaskTotals { TotalTasks = 100 },
                    Throughput = new List<ThroughputPoint>
                    {
                        new()
                        {
                            Second = 1,
                            ConnectionsCreated = 500,
                            ConnectionsReady = 500,
                            ActiveReady = 4000,
                            InFlightTasks = 4000,
                        },
                    },
                };
                File.WriteAllText(Path.Combine(dir, $"h{hostId}.json"), r.ToJson());
            }

            var report = Merger.Merge(dir, "camp", concurrentTarget: 11000, churnTarget: 1200);
            var g = report.Groups.Single();

            Assert.Equal(1500, g.PeakCombinedConnPerSec);      // 3 x 500
            Assert.Equal(1500, g.PeakCombinedReadyPerSec);     // 3 x 500 ready
            Assert.Equal(12000, g.PeakCombinedActiveReady);    // 3 x 4000 concurrent ready
            Assert.True(g.ReachedChurnTarget);
            Assert.True(g.ReachedReadyChurnTarget);
            Assert.True(g.ReachedActiveReadyTarget);
            Assert.True(g.ReachedConcurrentTarget);            // authoritative verdict = active-ready
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InFlightTasks_Alone_DoNot_Satisfy_ConcurrencyTarget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmt-lc2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // High in-flight Task concurrency (5000/host) but LOW driver active-ready (100/host): a saturated
            // generator with a backend that is NOT actually holding 11,000 established connections.
            foreach (var hostId in new[] { 1, 2, 3 })
            {
                var r = new RunResult
                {
                    Target = "documentdb",
                    Scenario = "Burst",
                    HostId = hostId,
                    HostCount = 3,
                    RunTag = "camp",
                    StartedUnixSeconds = 1000,
                    IterationNumber = 1,
                    IterationCount = 1,
                    Totals = new TaskTotals { TotalTasks = 100 },
                    Throughput = new List<ThroughputPoint>
                    {
                        new() { Second = 1, ConnectionsCreated = 50, ConnectionsReady = 50, ActiveReady = 100, InFlightTasks = 5000 },
                    },
                };
                File.WriteAllText(Path.Combine(dir, $"h{hostId}.json"), r.ToJson());
            }

            var report = Merger.Merge(dir, "camp", concurrentTarget: 11000, churnTarget: 1200);
            var g = report.Groups.Single();

            Assert.Equal(15000, g.PeakCombinedInFlight);       // 3 x 5000 in-flight Tasks
            Assert.Equal(300, g.PeakCombinedActiveReady);      // 3 x 100 actually-ready connections
            Assert.False(g.ReachedConcurrentTarget);           // in-flight tasks are NOT connection proof
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
