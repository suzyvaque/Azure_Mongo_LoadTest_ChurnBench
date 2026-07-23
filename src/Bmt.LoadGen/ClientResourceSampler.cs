using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Bmt.Core.Metrics;

namespace Bmt.LoadGen;

/// <summary>
/// Samples client-host resources (§7.3) AND §4 target-specific TCP telemetry. TCP state is sampled at a
/// fast raw cadence (250–500 ms) and aggregated into one-second buckets that retain the SUB-SECOND PEAK
/// of each counter, so short spikes are not averaged away. Only sockets whose remote endpoint matches the
/// resolved target IP/port set are counted as Target*; host-wide totals are kept separately as general
/// VM-pressure context. Heavier process metrics (CPU/mem/handles) are sampled once per second.
///
/// <para><b>Telemetry integrity + overhead</b>: driver events are captured immediately elsewhere; this
/// sampler only reads TCP tables and process counters — it holds no per-connection objects, does no
/// synchronous file I/O, and flushes summaries after the iteration. Dropped raw samples (enumeration
/// errors/timeouts) are counted and reported. Expected overhead: one <c>GetActiveTcpConnections()</c>
/// table read every 250 ms plus a per-second process refresh — well under 1% CPU on the generator VMs
/// at the churn rate; it does not touch the workload's connection or task paths.</para>
/// </summary>
public sealed class ClientResourceSampler : IAsyncDisposable
{
    private readonly int _rawIntervalMs;
    private readonly int _refreshSeconds;
    private readonly Stopwatch _clock;
    private readonly TargetEndpointResolver? _resolver;

    private readonly List<ResourceSample> _samples = new();
    private readonly ConcurrentDictionary<int, TcpSecondPeak> _tcpSeconds = new();
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _stopped;
    private bool _disposed;

    private TimeSpan _lastCpu;
    private DateTime _lastWall;
    private int _lastProcessSecond = -1;
    private long _lastRefreshSecond;
    private int _droppedSamples;

    private readonly ProcessSummary _peaks = new();
    private readonly int _ephemeralStart;
    private readonly int _ephemeralEnd;

    public ClientResourceSampler(int rawIntervalMs, Stopwatch runClock, TargetEndpointResolver? resolver = null, int refreshSeconds = 30)
    {
        // Raw cadence must sit in the §4 250–500 ms window. The config value historically meant the
        // per-second reporting period (often 1000 ms), which is NOT a valid raw cadence — only accept it
        // when it already falls in range, otherwise default to the fast end (250 ms).
        _rawIntervalMs = rawIntervalMs is >= 250 and <= 500 ? rawIntervalMs : 250;
        _refreshSeconds = Math.Max(5, refreshSeconds);
        _clock = runClock ?? throw new ArgumentNullException(nameof(runClock));
        _resolver = resolver;
        _lastCpu = _process.TotalProcessorTime;
        _lastWall = DateTime.UtcNow;
        (_ephemeralStart, _ephemeralEnd) = ReadEphemeralRange();
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        // Fixed-deadline scheduling so start-to-start cadence stays at the raw interval regardless of how
        // long each table read takes. If a sample overruns its deadline (a slow TCP-table read under load),
        // count it as a dropped interval so telemetry integrity is visible.
        var next = _clock.Elapsed + TimeSpan.FromMilliseconds(_rawIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var second = (int)_clock.Elapsed.TotalSeconds;
                SampleTcp(second);

                // Heavier process metrics only once per second.
                if (second != _lastProcessSecond)
                {
                    _lastProcessSecond = second;
                    SampleProcess(second);
                }

                // Periodically re-resolve managed endpoints (SRV/DNS can change).
                if (_resolver is not null && second - Interlocked.Read(ref _lastRefreshSecond) >= _refreshSeconds)
                {
                    Interlocked.Exchange(ref _lastRefreshSecond, second);
                    _ = _resolver.RefreshAsync(ct);
                }

                var wait = next - _clock.Elapsed;
                if (wait <= TimeSpan.Zero)
                {
                    // Overran the deadline — one raw interval was missed.
                    Interlocked.Increment(ref _droppedSamples);
                    next = _clock.Elapsed + TimeSpan.FromMilliseconds(_rawIntervalMs);
                }
                else
                {
                    next += TimeSpan.FromMilliseconds(_rawIntervalMs);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
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

    /// <summary>One raw TCP-table read: classify target vs host-wide sockets by state; fold sub-second peaks.</summary>
    private void SampleTcp(int second)
    {
        TcpConnectionInformation[] conns;
        try
        {
            conns = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _droppedSamples);
            return;
        }

        var snap = new TcpSnapshot();
        HashSet<int>? targetLocalPorts = null;
        HashSet<int>? ephemeralLocalPorts = null;

        foreach (var c in conns)
        {
            var state = c.State;
            if (state != TcpState.Closed)
            {
                snap.HostTotalTcpSockets++;
            }

            if (state == TcpState.TimeWait)
            {
                snap.HostTotalTimeWait++;
            }

            var localPort = c.LocalEndPoint?.Port ?? 0;
            if (localPort >= _ephemeralStart && localPort <= _ephemeralEnd)
            {
                (ephemeralLocalPorts ??= new HashSet<int>()).Add(localPort);
            }

            if (_resolver is null || !_resolver.Matches(c.RemoteEndPoint as IPEndPoint))
            {
                continue;
            }

            snap.TargetTotalSockets++;
            (targetLocalPorts ??= new HashSet<int>()).Add(localPort);
            switch (state)
            {
                case TcpState.SynSent: snap.TargetSynSent++; break;
                case TcpState.Established: snap.TargetEstablished++; break;
                case TcpState.TimeWait: snap.TargetTimeWait++; break;
                case TcpState.CloseWait: snap.TargetCloseWait++; break;
                case TcpState.FinWait1: snap.TargetFinWait1++; break;
                case TcpState.FinWait2: snap.TargetFinWait2++; break;
            }
        }

        snap.TargetDistinctLocalPorts = targetLocalPorts?.Count ?? 0;
        snap.EphemeralPortsInUse = ephemeralLocalPorts?.Count ?? 0;

        var bucket = _tcpSeconds.GetOrAdd(second, _ => new TcpSecondPeak());
        bucket.Fold(snap, _ephemeralStart, _ephemeralEnd);
    }

    private void SampleProcess(int second)
    {
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

        // Host TIME_WAIT / ephemeral snapshot for the legacy §7.3 ResourceSample (host-wide context).
        var (ephemeral, timeWait) = QuickHostTcp();

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

    /// <summary>Cheap host-wide TCP snapshot (non-closed + TIME_WAIT) for the legacy §7.3 sample.</summary>
    private static (int Ephemeral, int TimeWait) QuickHostTcp()
    {
        try
        {
            var conns = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
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

    /// <summary>Best-effort read of the Windows dynamic (ephemeral) TCP port range; defaults on failure.</summary>
    private static (int Start, int End) ReadEphemeralRange()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "int ipv4 show dynamicportrange tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var start = Regex.Match(output, @"Start Port\s*:\s*(\d+)");
                var count = Regex.Match(output, @"Number of Ports\s*:\s*(\d+)");
                if (start.Success && count.Success)
                {
                    var s = int.Parse(start.Groups[1].Value);
                    var n = int.Parse(count.Groups[1].Value);
                    return (s, s + n - 1);
                }
            }
        }
        catch
        {
            // fall through to default
        }

        return (49152, 65535);
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

    /// <summary>Per-second target TCP samples (sub-second peaks), ordered by second.</summary>
    public IReadOnlyList<TargetTcpSample> TargetTcpSamples() =>
        _tcpSeconds.OrderBy(kv => kv.Key).Select(kv => kv.Value.ToSample(kv.Key)).ToList();

    /// <summary>Resolved endpoint set + ephemeral range + telemetry-integrity metadata and peaks.</summary>
    public TargetTcpInfo TargetTcpInfo()
    {
        var samples = TargetTcpSamples();
        var info = new TargetTcpInfo
        {
            ResolvedAtUtc = _resolver?.ResolvedAtUtc ?? string.Empty,
            Endpoints = _resolver?.EndpointDescriptions.ToList() ?? new List<string>(),
            EndpointCount = _resolver?.EndpointCount ?? 0,
            EphemeralRangeStart = _ephemeralStart,
            EphemeralRangeEnd = _ephemeralEnd,
            DroppedSamples = Volatile.Read(ref _droppedSamples),
            RawSampleIntervalMs = _rawIntervalMs,
            OverheadNote = "One GetActiveTcpConnections() table read per raw interval + one process refresh " +
                           "per second; no per-connection retention, no synchronous I/O on workload paths.",
        };

        foreach (var s in samples)
        {
            info.PeakTargetSynSent = Math.Max(info.PeakTargetSynSent, s.TargetSynSent);
            info.PeakTargetEstablished = Math.Max(info.PeakTargetEstablished, s.TargetEstablished);
            info.PeakTargetTimeWait = Math.Max(info.PeakTargetTimeWait, s.TargetTimeWait);
            info.PeakTargetCloseWait = Math.Max(info.PeakTargetCloseWait, s.TargetCloseWait);
            info.PeakTargetFinWait1 = Math.Max(info.PeakTargetFinWait1, s.TargetFinWait1);
            info.PeakTargetFinWait2 = Math.Max(info.PeakTargetFinWait2, s.TargetFinWait2);
            info.PeakTargetTotalSockets = Math.Max(info.PeakTargetTotalSockets, s.TargetTotalSockets);
            info.PeakTargetDistinctLocalPorts = Math.Max(info.PeakTargetDistinctLocalPorts, s.TargetDistinctLocalPorts);
            info.PeakHostTotalTcpSockets = Math.Max(info.PeakHostTotalTcpSockets, s.HostTotalTcpSockets);
            info.PeakHostTotalTimeWait = Math.Max(info.PeakHostTotalTimeWait, s.HostTotalTimeWait);
            info.PeakEphemeralPortsInUse = Math.Max(info.PeakEphemeralPortsInUse, s.EphemeralPortsInUse);
            info.PeakEphemeralUtilizationPct = Math.Max(info.PeakEphemeralUtilizationPct, s.EphemeralUtilizationPct);
        }

        return info;
    }

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

    /// <summary>Immutable per-raw-sample TCP snapshot.</summary>
    private sealed class TcpSnapshot
    {
        public int TargetSynSent;
        public int TargetEstablished;
        public int TargetTimeWait;
        public int TargetCloseWait;
        public int TargetFinWait1;
        public int TargetFinWait2;
        public int TargetTotalSockets;
        public int TargetDistinctLocalPorts;
        public int HostTotalTcpSockets;
        public int HostTotalTimeWait;
        public int EphemeralPortsInUse;
    }

    /// <summary>Per-second sub-second-peak accumulator (max of each counter across the second's raw samples).</summary>
    private sealed class TcpSecondPeak
    {
        private readonly object _lock = new();
        private readonly TcpSnapshot _max = new();
        private int _rangeSize = 1;

        public void Fold(TcpSnapshot s, int ephemeralStart, int ephemeralEnd)
        {
            lock (_lock)
            {
                _rangeSize = Math.Max(1, ephemeralEnd - ephemeralStart + 1);
                _max.TargetSynSent = Math.Max(_max.TargetSynSent, s.TargetSynSent);
                _max.TargetEstablished = Math.Max(_max.TargetEstablished, s.TargetEstablished);
                _max.TargetTimeWait = Math.Max(_max.TargetTimeWait, s.TargetTimeWait);
                _max.TargetCloseWait = Math.Max(_max.TargetCloseWait, s.TargetCloseWait);
                _max.TargetFinWait1 = Math.Max(_max.TargetFinWait1, s.TargetFinWait1);
                _max.TargetFinWait2 = Math.Max(_max.TargetFinWait2, s.TargetFinWait2);
                _max.TargetTotalSockets = Math.Max(_max.TargetTotalSockets, s.TargetTotalSockets);
                _max.TargetDistinctLocalPorts = Math.Max(_max.TargetDistinctLocalPorts, s.TargetDistinctLocalPorts);
                _max.HostTotalTcpSockets = Math.Max(_max.HostTotalTcpSockets, s.HostTotalTcpSockets);
                _max.HostTotalTimeWait = Math.Max(_max.HostTotalTimeWait, s.HostTotalTimeWait);
                _max.EphemeralPortsInUse = Math.Max(_max.EphemeralPortsInUse, s.EphemeralPortsInUse);
            }
        }

        public TargetTcpSample ToSample(int second)
        {
            lock (_lock)
            {
                return new TargetTcpSample
                {
                    Second = second,
                    TargetSynSent = _max.TargetSynSent,
                    TargetEstablished = _max.TargetEstablished,
                    TargetTimeWait = _max.TargetTimeWait,
                    TargetCloseWait = _max.TargetCloseWait,
                    TargetFinWait1 = _max.TargetFinWait1,
                    TargetFinWait2 = _max.TargetFinWait2,
                    TargetTotalSockets = _max.TargetTotalSockets,
                    TargetDistinctLocalPorts = _max.TargetDistinctLocalPorts,
                    HostTotalTcpSockets = _max.HostTotalTcpSockets,
                    HostTotalTimeWait = _max.HostTotalTimeWait,
                    EphemeralPortsInUse = _max.EphemeralPortsInUse,
                    EphemeralUtilizationPct = Math.Round(100.0 * _max.EphemeralPortsInUse / _rangeSize, 3),
                };
            }
        }
    }
}
