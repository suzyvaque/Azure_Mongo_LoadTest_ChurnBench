using Bmt.Core;

namespace Bmt.LoadGen.Scenarios;

/// <summary>
/// Launches Tasks subject to a hard concurrency cap (<see cref="MaxConcurrentTasks"/>). Each launched
/// Task runs fire-and-forget on the thread pool; the launcher tracks them so a scenario can drain
/// (await all in-flight Tasks) at the end. Used by both the steady and burst scenarios so the no-reuse
/// per-Task connection model and the §6.2 concurrency ceiling are enforced uniformly.
/// </summary>
public sealed class TaskLauncher : IDisposable
{
    private readonly TaskRunner _runner;
    private readonly SemaphoreSlim _gate;
    private readonly Func<string> _reqIdSelector;
    private readonly List<Task> _inFlight = new();
    private readonly object _trackGate = new();

    public TaskLauncher(TaskRunner runner, int maxConcurrentTasks, Func<string> reqIdSelector)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _reqIdSelector = reqIdSelector ?? throw new ArgumentNullException(nameof(reqIdSelector));
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrentTasks));
    }

    /// <summary>
    /// Inject one Task. When <paramref name="gated"/> is true (closed-loop), this blocks (asynchronously)
    /// once the concurrency cap is reached — back-pressure that keeps the client host from unbounded
    /// socket growth while still surfacing real saturation. When false (open-loop), the gate is bypassed
    /// so the realized injection rate matches the offered schedule regardless of backend completion speed.
    /// </summary>
    public async Task InjectAsync(CancellationToken ct, bool gated = true)
    {
        // "Offered to the runtime" = the instant the scenario hands this Task to the launcher per its
        // arrival schedule (the deadline-checked decision happens in the scenario loop just before this
        // call). Stamp the scheduled instant BEFORE any closed-loop gate wait so it stays within the
        // arrival window even under back-pressure, and so scheduler-queue latency folds in generator
        // back-pressure (a load-generator-runtime delay) instead of hiding it. Open-loop has no gate, so
        // this is identical to stamping at dispatch. The scheduled COUNTER is incremented only once the
        // Task is actually dispatched (below), so a gate wait cancelled at shutdown never leaks a
        // scheduled-but-never-run Task (keeps scheduled ≈ started ≈ finished reconciling).
        var scheduledUtc = DateTime.UtcNow;
        var reqId = _reqIdSelector();

        if (gated)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }

        _runner.OnScheduled();
        var task = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(reqId, scheduledUtc, ct).ConfigureAwait(false);
            }
            finally
            {
                if (gated)
                {
                    _gate.Release();
                }
            }
        }, CancellationToken.None);

        lock (_trackGate)
        {
            _inFlight.Add(task);
            // Periodically prune completed Tasks so the tracking list does not grow unbounded over an hour.
            if (_inFlight.Count >= 4096)
            {
                _inFlight.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    /// <summary>Await all launched Tasks to complete (drain phase after the arrival generator stops).</summary>
    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_trackGate)
        {
            pending = _inFlight.ToArray();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();
}
