using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Errors;

namespace Bmt.Preflight;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var options = PreflightOptions.Parse(args);
            if (options.ShowHelp)
            {
                PreflightOptions.PrintUsage();
                return 0;
            }

            var config = BmtConfig.Load(options.ConfigPath);
            var runner = new PreflightRunner(config, options.Target, options.Warmup, options.VerifyDistinct);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                ConsoleLog.Warn("Cancellation requested — aborting preflight.");
            };

            var report = await runner.RunAsync(cts.Token).ConfigureAwait(false);

            var jsonPath = options.JsonPath ?? DefaultJsonPath(config, report);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            await File.WriteAllTextAsync(jsonPath, report.ToJson(), cts.Token).ConfigureAwait(false);

            PrintSummary(report, jsonPath);

            // Exit code: 0 = may proceed (all Pass, or Warn-only); 3 = a check failed, do NOT run.
            return report.CanProceed ? 0 : 3;
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Warn("preflight canceled.");
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
            PreflightOptions.PrintUsage();
            return 64;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Unhandled error: {ex.Message}");
            ConsoleLog.Error(ex.ToString());
            return 1;
        }
    }

    private static string DefaultJsonPath(BmtConfig config, PreflightReport report)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(config.Preflight.ResultsDirectory, $"preflight-{report.Target}-{stamp}.json");
    }

    private static void PrintSummary(PreflightReport report, string jsonPath)
    {
        var pass = report.Checks.Count(c => c.Status == PreflightStatus.Pass);
        var warn = report.Checks.Count(c => c.Status == PreflightStatus.Warn);
        var fail = report.Checks.Count(c => c.Status == PreflightStatus.Fail);

        ConsoleLog.Info(new string('-', 70));
        ConsoleLog.Info($"PREFLIGHT {report.Outcome.ToString().ToUpperInvariant()} for {report.Target}: " +
                        $"{pass} pass / {warn} warn / {fail} fail");
        if (report.IndexPolicy.InputIndexPresent && report.IndexPolicy.UniquenessDivergesFromCanonical)
        {
            ConsoleLog.Warn("ReqId-index uniqueness DIVERGENCE recorded: calc_input is NON-UNIQUE on this target " +
                            "(cosmos-ru platform constraint). " + report.IndexPolicy.DistinctReqIdGuarantee);
        }

        ConsoleLog.Info($"Report written: {jsonPath}");
        if (report.CanProceed)
        {
            ConsoleLog.Info(warn > 0
                ? "Gate: MAY PROCEED (warnings present -- review before the timed run)."
                : "Gate: MAY PROCEED.");
        }
        else
        {
            ConsoleLog.Error("Gate: ABORT — at least one check FAILED. Do not start the timed run (results would be invalid).");
        }
    }
}

/// <summary>Parsed CLI options for <c>preflight --config &lt;path&gt; --target &lt;key&gt; [--warmup] [--verify-distinct] [--json &lt;path&gt;]</c>.</summary>
internal sealed class PreflightOptions
{
    public string ConfigPath { get; private set; } = "config.json";

    public TargetKey Target { get; private set; }

    public bool Warmup { get; private set; }

    public bool VerifyDistinct { get; private set; }

    public string? JsonPath { get; private set; }

    public bool ShowHelp { get; private set; }

    public static PreflightOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new PreflightOptions();
        string? targetRaw = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "preflight":
                    break;
                case "--config":
                case "-c":
                    options.ConfigPath = RequireValue(args, ref i, arg);
                    break;
                case "--target":
                case "-t":
                    targetRaw = RequireValue(args, ref i, arg);
                    break;
                case "--json":
                case "-o":
                    options.JsonPath = RequireValue(args, ref i, arg);
                    break;
                case "--warmup":
                    options.Warmup = true;
                    break;
                case "--verify-distinct":
                    options.VerifyDistinct = true;
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
            throw new ArgumentException("Missing required --target (cosmos-ru | documentdb | mongo-vm).");
        }

        options.Target = TargetConnection.Parse(targetRaw);
        return options;
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
        Console.WriteLine("Usage: preflight --config <config.json> --target <cosmos-ru|documentdb|mongo-vm> [options]");
        Console.WriteLine();
        Console.WriteLine("  --config, -c        Path to config.json (default: config.json).");
        Console.WriteLine("  --target, -t        Backend key (resolves the connection string from its env var).");
        Console.WriteLine("  --json,   -o        Output path for the preflight JSON artifact (default: artifacts/preflight-<target>-<ts>.json).");
        Console.WriteLine("  --warmup            Perform the untimed data-cache warm-up sweep and write the warm-up sentinel (§6.5).");
        Console.WriteLine("  --verify-distinct   Fully verify distinct ReqId == document count via aggregation (RU-heavy on cosmos-ru).");
        Console.WriteLine("  --help,   -h        Show this help.");
        Console.WriteLine();
        Console.WriteLine("Runs the ten §6.3 preflight checks and gates the timed run. Exit 0 = may proceed (pass/warn),");
        Console.WriteLine("3 = a check failed (abort). The unique-vs-non-unique ReqId-index divergence (cosmos-ru) and the");
        Console.WriteLine("distinct-ReqId guarantee are recorded in the JSON artifact for the report.");
    }
}
