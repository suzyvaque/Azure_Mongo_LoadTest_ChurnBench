using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bmt.Preflight.Net;

/// <summary>
/// Reads the Windows client-host TCP churn headroom (§7.3): the ephemeral (dynamic) port range and
/// <c>TcpTimedWaitDelay</c>. These bound how many short-lived connections per second the host can
/// open before exhausting ports / piling up TIME_WAIT — the core risk of a connection-churn test.
/// Uses <c>netsh</c> and <c>reg query</c> (no registry API / platform-guard warnings).
/// </summary>
internal static partial class HostHeadroom
{
    /// <summary>Modern Windows default TIME_WAIT when the registry value is unset (seconds).</summary>
    private const int DefaultTimedWaitDelaySeconds = 120;

    public sealed record Result(
        int EphemeralPortStart,
        int EphemeralPortCount,
        int TcpTimedWaitDelaySeconds,
        bool TcpTimedWaitDelayIsDefault);

    public static async Task<Result> ReadAsync(CancellationToken ct)
    {
        var (start, count) = await ReadDynamicPortRangeAsync(ct).ConfigureAwait(false);
        var (delay, isDefault) = await ReadTimedWaitDelayAsync(ct).ConfigureAwait(false);
        return new Result(start, count, delay, isDefault);
    }

    private static async Task<(int start, int count)> ReadDynamicPortRangeAsync(CancellationToken ct)
    {
        var output = await RunAsync("netsh", "int ipv4 show dynamicport tcp", ct).ConfigureAwait(false);
        var start = ParseInt(StartPortRegex().Match(output));
        var count = ParseInt(NumberOfPortsRegex().Match(output));
        return (start, count);
    }

    private static async Task<(int delay, bool isDefault)> ReadTimedWaitDelayAsync(CancellationToken ct)
    {
        var output = await RunAsync(
            "reg",
            @"query ""HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters"" /v TcpTimedWaitDelay",
            ct).ConfigureAwait(false);

        var match = TimedWaitRegex().Match(output);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds))
        {
            return (seconds, false);
        }

        return (DefaultTimedWaitDelaySeconds, true);
    }

    private static int ParseInt(Match m) =>
        m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;

    private static async Task<string> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return stdout;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"Start Port\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StartPortRegex();

    [GeneratedRegex(@"Number of Ports\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberOfPortsRegex();

    [GeneratedRegex(@"TcpTimedWaitDelay\s+REG_DWORD\s+0x([0-9a-fA-F]+)")]
    private static partial Regex TimedWaitRegex();
}
