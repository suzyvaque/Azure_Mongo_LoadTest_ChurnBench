using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Errors;

namespace Bmt.Seeder;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = SeederOptions.Parse(args);
            if (options.ShowHelp)
            {
                SeederOptions.PrintUsage();
                return 0;
            }

            var config = BmtConfig.Load(options.ConfigPath);
            var runner = new SeedRunner(config, options.Target, options.Force);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                ConsoleLog.Warn("Cancellation requested — finishing current batch then stopping (re-run to resume).");
            };

            await runner.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Warn("prepare-data canceled. Seeding is resumable — re-run the same command.");
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
            SeederOptions.PrintUsage();
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

/// <summary>Parsed CLI options for <c>prepare-data --config &lt;path&gt; --target &lt;key&gt; [--force]</c>.</summary>
internal sealed class SeederOptions
{
    public string ConfigPath { get; private set; } = "config.json";

    public TargetKey Target { get; private set; }

    public bool Force { get; private set; }

    public bool ShowHelp { get; private set; }

    public static SeederOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new SeederOptions();
        string? targetRaw = null;
        var targetSeen = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "prepare-data":
                    // Accept the verb so callers can use the same surface as the unified CLI.
                    break;
                case "--config":
                case "-c":
                    options.ConfigPath = RequireValue(args, ref i, arg);
                    break;
                case "--target":
                case "-t":
                    targetRaw = RequireValue(args, ref i, arg);
                    targetSeen = true;
                    break;
                case "--force":
                case "-f":
                    options.Force = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    return options;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (!targetSeen || targetRaw is null)
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
        Console.WriteLine("Usage: seeder prepare-data --config <config.json> --target <cosmos-ru|documentdb|mongo-vm> [--force]");
        Console.WriteLine();
        Console.WriteLine("  --config, -c   Path to config.json (default: config.json).");
        Console.WriteLine("  --target, -t   Backend key. Resolves the connection string from the matching env var:");
        Console.WriteLine("                   cosmos-ru  -> BMT_CONN_COSMOS");
        Console.WriteLine("                   documentdb -> BMT_CONN");
        Console.WriteLine("                   mongo-vm   -> BMT_CONN_MONGO");
        Console.WriteLine("  --force, -f    Empty calc_input first, then reseed from scratch.");
        Console.WriteLine("  --help, -h     Show this help.");
        Console.WriteLine();
        Console.WriteLine("Seeds exactly the configured doc count into calc_input (whole-doc BSON sizing) and");
        Console.WriteLine("creates the ReqId index on both calc_input (unique) and calc_output. Idempotent/resumable.");
    }
}
