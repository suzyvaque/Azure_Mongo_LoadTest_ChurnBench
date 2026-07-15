using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Errors;

namespace Bmt.LoadGen;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var options = RunOptions.Parse(args);
            if (options.ShowHelp)
            {
                RunOptions.PrintUsage();
                return 0;
            }

            var config = BmtConfig.Load(options.ConfigPath);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                ConsoleLog.Warn("Cancellation requested — stopping arrival generation and draining.");
            };

            var orchestrator = new RunOrchestrator(config, options);
            return await orchestrator.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Warn("loadgen canceled.");
            return 130;
        }
        catch (BmtException ex)
        {
            ConsoleLog.Error($"{ex.ErrorType}: {ex.Message}");
            return 2;
        }
        catch (ArgumentException ex)
        {
            ConsoleLog.Error(ex.Message);
            RunOptions.PrintUsage();
            return 64;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Unhandled error: {ex.Message}");
            ConsoleLog.Error(ex.ToString());
            return 1;
        }
    }
}

/// <summary>Which scenario(s) the run drives (test_instruction.md §6.2).</summary>
public enum RunScenario
{
    Steady,
    Burst,
    Both,
}

/// <summary>
/// Parsed CLI options for
/// <c>test --target &lt;key&gt; --scenario &lt;steady|burst|both&gt; [--config p] [--duration-sec N] [--no-preflight] [--results dir]</c>.
/// </summary>
public sealed class RunOptions
{
    public string ConfigPath { get; private set; } = "config.json";

    public TargetKey Target { get; private set; }

    public RunScenario Scenario { get; private set; } = RunScenario.Both;

    /// <summary>Optional override of each scenario's duration (seconds) — used for short smoke runs.</summary>
    public int DurationSecondsOverride { get; private set; }

    public bool RunPreflight { get; private set; } = true;

    public string ResultsDirectory { get; private set; } = "results";

    /// <summary>1-based id of this load-generating host within a coordinated multi-host campaign.</summary>
    public int HostId { get; private set; } = 1;

    /// <summary>Total number of load-generating hosts in the campaign (per-host share = total/HostCount).</summary>
    public int HostCount { get; private set; } = 1;

    /// <summary>Shared campaign tag so per-host artifacts can be grouped by the <c>merge</c> command.</summary>
    public string RunTag { get; private set; } = string.Empty;

    /// <summary>
    /// Optional UTC instant (ISO-8601) at which the timed phase must begin. All hosts pass the SAME
    /// value so their bursts are wall-clock aligned and their concurrency/conn-s provably sum. Null =
    /// start immediately.
    /// </summary>
    public DateTime? StartAtUtc { get; private set; }

    public bool ShowHelp { get; private set; }

    public static RunOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new RunOptions();
        string? targetRaw = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "test":
                    break;
                case "--config":
                case "-c":
                    options.ConfigPath = RequireValue(args, ref i, arg);
                    break;
                case "--target":
                case "-t":
                    targetRaw = RequireValue(args, ref i, arg);
                    break;
                case "--scenario":
                case "-s":
                    options.Scenario = ParseScenario(RequireValue(args, ref i, arg));
                    break;
                case "--duration-sec":
                case "-d":
                    options.DurationSecondsOverride = ParseDuration(RequireValue(args, ref i, arg));
                    break;
                case "--results":
                    options.ResultsDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--host-id":
                    options.HostId = ParsePositive(RequireValue(args, ref i, arg), arg);
                    break;
                case "--host-count":
                    options.HostCount = ParsePositive(RequireValue(args, ref i, arg), arg);
                    break;
                case "--run-tag":
                    options.RunTag = RequireValue(args, ref i, arg);
                    break;
                case "--start-at":
                    options.StartAtUtc = ParseUtcInstant(RequireValue(args, ref i, arg));
                    break;
                case "--no-preflight":
                    options.RunPreflight = false;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    return options;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (targetRaw is null)
        {
            throw new ArgumentException("Missing required --target (cosmos-ru | documentdb | mongo-vm | mongo-shard).");
        }

        options.Target = TargetConnection.Parse(targetRaw);
        if (options.HostId > options.HostCount)
        {
            throw new ArgumentException(
                $"--host-id ({options.HostId}) must be <= --host-count ({options.HostCount}).");
        }

        return options;
    }

    private static RunScenario ParseScenario(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "steady" or "a" => RunScenario.Steady,
        "burst" or "b" => RunScenario.Burst,
        "both" => RunScenario.Both,
        _ => throw new ArgumentException($"Unknown --scenario '{raw}'. Expected steady | burst | both."),
    };

    private static int ParseDuration(string raw)
    {
        if (!int.TryParse(raw, out var seconds) || seconds <= 0)
        {
            throw new ArgumentException($"--duration-sec must be a positive integer (got '{raw}').");
        }

        return seconds;
    }

    private static int ParsePositive(string raw, string flag)
    {
        if (!int.TryParse(raw, out var v) || v <= 0)
        {
            throw new ArgumentException($"{flag} must be a positive integer (got '{raw}').");
        }

        return v;
    }

    private static DateTime ParseUtcInstant(string raw)
    {
        if (!DateTime.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
        {
            throw new ArgumentException($"--start-at must be an ISO-8601 UTC instant (got '{raw}').");
        }

        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {flag}.");
        }

        return args[++i];
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: test --target <cosmos-ru|documentdb|mongo-vm|mongo-shard> --scenario <steady|burst|both> [options]");
        Console.WriteLine();
        Console.WriteLine("  --config, -c        Path to config.json (default: config.json).");
        Console.WriteLine("  --target, -t        Backend key (resolves the connection string from its env var).");
        Console.WriteLine("  --scenario, -s      steady (A) | burst (B) | both (default: both).");
        Console.WriteLine("  --duration-sec, -d  Override each iteration's duration in seconds (for short smoke runs).");
        Console.WriteLine("  --results           Output directory for campaign folder (default: results).");
        Console.WriteLine("  --host-id N         1-based id of this generator host in a multi-host burst (default 1).");
        Console.WriteLine("  --host-count M      Total generator hosts in the campaign (default 1). Per-host churn/");
        Console.WriteLine("                      concurrent preflight target = ceil(total / M). Seeds are offset by host.");
        Console.WriteLine("  --run-tag TAG       Shared campaign tag stamped into artifacts so `report merge` can group hosts.");
        Console.WriteLine("  --start-at UTC      ISO-8601 UTC instant to begin the timed phase (all hosts pass the SAME");
        Console.WriteLine("                      value so bursts align and conn/s + concurrency provably sum across hosts).");
        Console.WriteLine("  --no-preflight      Skip the §6.3 preflight gate (NOT recommended; preconditions unverified).");
        Console.WriteLine("  --help,   -h        Show this help.");
        Console.WriteLine();
        Console.WriteLine("Config controls workload shape (config.json):");
        Console.WriteLine("  Scenario.Iterations            Number of back-to-back timed windows (default 1; production uses 3).");
        Console.WriteLine("  Scenario.IterationDurationSeconds  Duration per iteration, overrides per-scenario DurationSeconds.");
        Console.WriteLine("  Workload.Mode                  FullWorkload (default) | SingleOp");
        Console.WriteLine("  Workload.SingleOpType          FindInput | InsertOutput  (used when Mode=SingleOp)");
        Console.WriteLine();
        Console.WriteLine("Artifact layout:");
        Console.WriteLine("  results/<target>-<scenario>-<workload>-<stamp>/");
        Console.WriteLine("    iter-01/  <runid>-iter-01-<stamp>.json  + -timeseries.csv  + -latency.csv");
        Console.WriteLine("    iter-02/  ...");
        Console.WriteLine("    aggregate.json  (cross-iteration mean/min/max stats)");
    }
}
