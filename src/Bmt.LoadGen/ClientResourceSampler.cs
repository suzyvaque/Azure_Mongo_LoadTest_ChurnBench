using System.Diagnostics;
using System.Net.NetworkInformation;
using Bmt.Core.Metrics;

namespace Bmt.LoadGen;

/// <summary>
/// Periodically samples the §7.3 client-host resources that bound connection-churn workloads:
/// ephemeral ports in use, sockets in TIME_WAIT, process handle/thread counts, CPU %, and working
/// set. These are reported SEPARATELY from server-side errors so a client-side limit (e.g. port
/// exhaustion) is never misattributed to the database. Runs on a background loop until stopped.
/// </summary>
public sealed class ClientResourceSampler : IAsyncDisposable
{
    private readonly int _intervalMs;
    private readonly Stopwatch _clock;
    private readonly List<ResourceSample> _samples = new();
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _stopped;
    private bool _disposed;

    private TimeSpan _lastCpu;
    private DateTime _lastWall;

    private readonly ProcessSummary _peaks = new();

    public ClientResourceSampler(int intervalMs, Stopwatch runClock)
    {
        _intervalMs = intervalMs <= 0 ? 1000 : intervalMs;
        _clock = runClock ?? throw new ArgumentNullException(nameof(runClock));
        _lastCpu = _process.TotalProcessorTime;
        _lastWall = DateTime.UtcNow;
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sample();
                await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn($"resource sampler error: {ex.Message}");
            }
        }
    }

    private void Sample()
    {
        var second = (int)_clock.Elapsed.TotalSeconds;

        var (ephemeral, timeWait) = SampleTcp();

        _process.Refresh();
        var handles = _process.HandleCount;
        var threads = _process.Threads.Count;
        var workingSet = _process.WorkingSet64;

        var nowCpu = _process.TotalProcessorTime;
        var nowWall = DateTime.UtcNow;
        var cpuDelta = (nowCpu - _lastCpu).TotalMilliseconds;
        var wallDelta = (nowWall - _lastWall).TotalMilliseconds;
        var cpuPercent = wallDelta > 0
            ? Math.Clamp(cpuDelta / (wallDelta * Environment.ProcessorCount) * 100.0, 0, 100)
            : 0;
        _lastCpu = nowCpu;
        _lastWall = nowWall;

        var sample = new ResourceSample
        {
            Second = second,
            EphemeralPortsInUse = ephemeral,
            TimeWaitSockets = timeWait,
            HandleCount = handles,
            ThreadCount = threads,
            CpuPercent = Math.Round(cpuPercent, 2),
            WorkingSetBytes = workingSet,
        };

        lock (_gate)
        {
            _samples.Add(sample);
            _peaks.PeakEphemeralPortsInUse = Math.Max(_peaks.PeakEphemeralPortsInUse, ephemeral);
            _peaks.PeakTimeWaitSockets = Math.Max(_peaks.PeakTimeWaitSockets, timeWait);
            _peaks.PeakHandleCount = Math.Max(_peaks.PeakHandleCount, handles);
            _peaks.PeakThreadCount = Math.Max(_peaks.PeakThreadCount, threads);
            _peaks.PeakWorkingSetBytes = Math.Max(_peaks.PeakWorkingSetBytes, workingSet);
            _peaks.MaxCpuPercent = Math.Max(_peaks.MaxCpuPercent, sample.CpuPercent);
        }
    }

    /// <summary>Count active TCP connections by state to surface ephemeral-port pressure + TIME_WAIT.</summary>
    private static (int Ephemeral, int TimeWait) SampleTcp()
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var conns = props.GetActiveTcpConnections();
            var timeWait = 0;
            var nonClosed = 0;
            foreach (var c in conns)
            {
                if (c.State == TcpState.TimeWait)
                {
                    timeWait++;
                }

                if (c.State != TcpState.Closed)
                {
                    nonClosed++;
                }
            }

            return (nonClosed, timeWait);
        }
        catch (NetworkInformationException)
        {
            return (0, 0);
        }
    }

    public IReadOnlyList<ResourceSample> Samples()
    {
        lock (_gate)
        {
            return _samples.ToList();
        }
    }

    public ProcessSummary Peaks()
    {
        lock (_gate)
        {
            return new ProcessSummary
            {
                PeakEphemeralPortsInUse = _peaks.PeakEphemeralPortsInUse,
                PeakTimeWaitSockets = _peaks.PeakTimeWaitSockets,
                PeakHandleCount = _peaks.PeakHandleCount,
                PeakThreadCount = _peaks.PeakThreadCount,
                PeakWorkingSetBytes = _peaks.PeakWorkingSetBytes,
                MaxCpuPercent = _peaks.MaxCpuPercent,
            };
        }
    }

    /// <summary>Halt sampling (so the final samples/peaks are stable) without disposing resources.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        try
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _process.Dispose();
    }
}
