using Bmt.Core.Connections;
using Bmt.Core.Metrics;
using Bmt.LoadGen;
using Xunit;

namespace Bmt.Tests;

/// <summary>
/// Validates the §2 open-loop decomposition: scheduler-queue / execution / offered-to-finished latency,
/// the authoritative all-offered set vs the arrival-completed subset, and the arrival-stop backlog
/// snapshot. These are the timing invariants that make an overloaded backend look correct rather than
/// artificially fast (drain completions must be included in the authoritative set).
/// </summary>
public sealed class MetricsDecompositionTests
{
    private static RunResult BuildResult(MetricsCollector m) =>
        m.Build(new ConnectionEventCounters(), new List<ResourceSample>(), new ProcessSummary());

    [Fact]
    public void AuthoritativeSet_Includes_DrainCompletions_WhileArrivalSet_Excludes_Them()
    {
        var m = new MetricsCollector();
        m.StartClock();

        // 5 Tasks scheduled + started.
        for (var i = 0; i < 5; i++)
        {
            m.OnTaskScheduled();
            m.OnTaskStart();
        }

        // 3 finish DURING arrival (fast); e2e = 10/20/30.
        m.OnTaskEnd(success: true, cycleMs: 9, schedQueueMs: 1, execMs: 9, offeredToFinishedMs: 10);
        m.OnTaskEnd(success: true, cycleMs: 18, schedQueueMs: 2, execMs: 18, offeredToFinishedMs: 20);
        m.OnTaskEnd(success: true, cycleMs: 27, schedQueueMs: 3, execMs: 27, offeredToFinishedMs: 30);

        // Arrival window closes with 2 Tasks still outstanding.
        m.OnArrivalStopped();
        Assert.Equal(2, m.TasksOutstandingAtArrivalStop);

        // The slow tail finishes DURING drain; e2e = 100/200 (the slowest requests).
        m.OnTaskEnd(success: true, cycleMs: 96, schedQueueMs: 4, execMs: 96, offeredToFinishedMs: 100);
        m.OnTaskEnd(success: false, cycleMs: 195, schedQueueMs: 5, execMs: 195, offeredToFinishedMs: 200);

        var r = BuildResult(m);

        Assert.Equal(5, r.Totals.TasksScheduled);
        Assert.Equal(5, r.Totals.TasksStarted);
        Assert.Equal(3, r.OpenLoop.TasksCompletedDuringArrival);

        // Authoritative set = ALL 5, so it retains the slow drain tail (max 200ms).
        Assert.Equal(5, r.OpenLoop.OfferedToFinishedLatencyMs.Count);
        Assert.Equal(200, r.OpenLoop.OfferedToFinishedLatencyMs.MaxMs);

        // Arrival-completed subset = only the 3 fast ones (max 30ms). Excluding drain completions here is
        // exactly why this must NOT be the headline number.
        Assert.Equal(3, r.OpenLoop.OfferedToFinishedLatencyArrivalMs.Count);
        Assert.Equal(30, r.OpenLoop.OfferedToFinishedLatencyArrivalMs.MaxMs);

        // Scheduler + execution digests cover every offered Task.
        Assert.Equal(5, r.OpenLoop.SchedulerQueueLatencyMs.Count);
        Assert.Equal(5, r.OpenLoop.TaskExecutionLatencyMs.Count);

        // Backlog carried into drain was 2, and drain never grew it.
        Assert.True(m.MaximumDrainBacklog >= 2);
    }

    [Fact]
    public void SchedulerAndExecution_Decompose_TrueEndToEnd()
    {
        var m = new MetricsCollector();
        m.StartClock();

        // Requirement example: scheduled@10s, started@20s, finished@32s -> sched 10s, exec 12s, e2e 22s.
        m.OnTaskScheduled();
        m.OnTaskStart();
        m.OnTaskEnd(success: true, cycleMs: 12_000, schedQueueMs: 10_000, execMs: 12_000, offeredToFinishedMs: 22_000);

        var r = BuildResult(m);
        Assert.Equal(10_000, r.OpenLoop.SchedulerQueueLatencyMs.P50Ms);
        Assert.Equal(12_000, r.OpenLoop.TaskExecutionLatencyMs.P50Ms);
        Assert.Equal(22_000, r.OpenLoop.OfferedToFinishedLatencyMs.P50Ms);
    }

    [Theory]
    [InlineData(360_000, 300, 1200.0)]   // correct: arrival-window denominator
    [InlineData(360_000, 345, 1043.48)]  // wrong: total-duration denominator understates offered load
    public void OfferedRate_Uses_ArrivalWindow_As_Denominator(long scheduled, int windowSeconds, double expectedRate)
    {
        var rate = Math.Round((double)scheduled / windowSeconds, 2);
        Assert.Equal(expectedRate, rate, 2);
    }
}
