using System.Diagnostics;
using Bmt.Core.Configuration;

namespace Bmt.LoadGen.Scenarios;

/// <summary>
/// Scenario B — Poisson-arrival burst (test_instruction.md §6.2): Jobs arrive as a Poisson process at
/// rate λ Jobs/sec (exponential inter-arrival times). Each Job injects N Tasks "at once"
/// (N ∈ [MinTasksPerJob, MaxTasksPerJob], cap 500) to recreate the production spike shape — driving
/// the ≥1,200 conn/sec instantaneous / ≥11,000 concurrent stress points. Injection of a Job's Tasks
/// is concurrent (not serialized) so the burst is genuinely simultaneous.
/// </summary>
public sealed class BurstScenario
{
    private readonly BurstScenarioConfig _config;
    private readonly int _durationSecondsOverride;
    private readonly Random _rng;

    public BurstScenario(BurstScenarioConfig config, int durationSecondsOverride = 0, int seed = 42)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _durationSecondsOverride = durationSecondsOverride;
        _rng = new Random(seed);
    }

    public async Task RunAsync(TaskLauncher launcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        var durationSeconds = _durationSecondsOverride > 0 ? _durationSecondsOverride : _config.DurationSeconds;
        var deadline = TimeSpan.FromSeconds(durationSeconds);

        ConsoleLog.Info($"[Burst] Poisson lambda={_config.JobsPerSecondLambda} Job/s, " +
                        $"{_config.MinTasksPerJob}..{_config.MaxTasksPerJob} Tasks/Job, for {durationSeconds}s.");

        var clock = Stopwatch.StartNew();
        var jobs = 0L;
        var tasks = 0L;

        while (!ct.IsCancellationRequested && clock.Elapsed < deadline)
        {
            var waitMs = NextExponentialIntervalMs(_config.JobsPerSecondLambda);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (clock.Elapsed >= deadline)
            {
                break;
            }

            var n = _rng.Next(_config.MinTasksPerJob, _config.MaxTasksPerJob + 1);
            jobs++;
            tasks += n;

            // Inject the Job's Tasks simultaneously (each InjectAsync may block on the concurrency cap;
            // we fire them concurrently so the burst is not serialized into a trickle).
            var injections = new Task[n];
            for (var i = 0; i < n; i++)
            {
                injections[i] = launcher.InjectAsync(ct);
            }

            try
            {
                await Task.WhenAll(injections).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ConsoleLog.Info($"[Burst] arrival generator stopped after {jobs} Jobs / {tasks} Tasks; draining in-flight.");
    }

    /// <summary>Exponential inter-arrival time for a Poisson process of rate λ (per second), in ms.</summary>
    private double NextExponentialIntervalMs(double lambdaPerSec)
    {
        var u = 1.0 - _rng.NextDouble();
        var seconds = -Math.Log(u) / lambdaPerSec;
        return seconds * 1000.0;
    }
}
