using System.Diagnostics;
using Bmt.Core.Configuration;

namespace Bmt.LoadGen.Scenarios;

/// <summary>
/// Scenario A — steady peak-hour load (test_instruction.md §6.2): inject new Tasks (each = a new
/// connection) at a constant <c>TasksPerSecond</c> for <c>DurationSeconds</c>. With the default
/// 10 s taskSleepMs, Little's Law puts steady concurrency at ≈ rate × hold (135 × 10 ≈ 1,350).
/// Arrivals are paced by a fixed inter-injection interval; the launcher's concurrency cap applies
/// back-pressure if the host saturates.
/// </summary>
public sealed class SteadyScenario
{
    private readonly SteadyScenarioConfig _config;
    private readonly int _durationSecondsOverride;

    public SteadyScenario(SteadyScenarioConfig config, int durationSecondsOverride = 0)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _durationSecondsOverride = durationSecondsOverride;
    }

    public async Task RunAsync(TaskLauncher launcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        var durationSeconds = _durationSecondsOverride > 0 ? _durationSecondsOverride : _config.DurationSeconds;
        var intervalMs = 1000.0 / _config.TasksPerSecond;
        var deadline = TimeSpan.FromSeconds(durationSeconds);

        ConsoleLog.Info($"[Steady] {_config.TasksPerSecond} Tasks/s for {durationSeconds}s " +
                        $"(~{(long)(_config.TasksPerSecond * durationSeconds)} Tasks).");

        var clock = Stopwatch.StartNew();
        var injected = 0L;
        var nextDue = 0.0;

        while (!ct.IsCancellationRequested && clock.Elapsed < deadline)
        {
            await launcher.InjectAsync(ct).ConfigureAwait(false);
            injected++;
            nextDue += intervalMs;

            var aheadMs = nextDue - clock.Elapsed.TotalMilliseconds;
            if (aheadMs > 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(aheadMs), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        ConsoleLog.Info($"[Steady] arrival generator stopped after {injected} Tasks; draining in-flight.");
    }
}
