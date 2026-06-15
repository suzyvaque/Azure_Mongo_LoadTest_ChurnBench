using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Bmt.Preflight.Net;

/// <summary>
/// Queries Windows time-sync status via <c>w32tm /query /status</c> for preflight check 8 (latency
/// and per-second-rate metrics depend on an accurate, NTP-synced clock).
/// </summary>
internal static partial class ClockStatus
{
    public sealed record Result(bool Synced, string Source, string LastSync);

    public static async Task<Result> ReadAsync(CancellationToken ct)
    {
        var output = await RunAsync("w32tm", "/query /status", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
        {
            return new Result(false, "unknown", "unknown");
        }

        var source = SourceRegex().Match(output) is { Success: true } s ? s.Groups[1].Value.Trim() : "unknown";
        var lastSync = LastSyncRegex().Match(output) is { Success: true } l ? l.Groups[1].Value.Trim() : "unknown";

        // An unsynced host typically reports a free-running local clock as its source.
        var synced = source.Length > 0 &&
                     !source.Contains("Local CMOS Clock", StringComparison.OrdinalIgnoreCase) &&
                     !source.Contains("Free-running", StringComparison.OrdinalIgnoreCase) &&
                     !source.Equals("unknown", StringComparison.OrdinalIgnoreCase);

        return new Result(synced, source, lastSync);
    }

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

    [GeneratedRegex(@"Source:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"Last Successful Sync Time:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex LastSyncRegex();
}
