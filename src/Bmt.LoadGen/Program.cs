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
