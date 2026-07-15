namespace Bmt.Report;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Length > 0 && string.Equals(args[0], "merge", StringComparison.OrdinalIgnoreCase))
            {
                return RunMerge(args);
            }

            var options = ReportOptions.Parse(args);
            if (options.ShowHelp)
            {
                ReportOptions.PrintUsage();
                return 0;
            }

            ConsoleLog.Info($"Loading run results from: {options.InputDir}");
            var targets = ReportLoader.Load(options.InputDir);
            if (targets.Count == 0)
            {
                ConsoleLog.Warn("No run-result JSON files found; writing an empty report shell.");
            }
            else
            {
                ConsoleLog.Info($"Loaded {targets.Count} run(s): " +
                                string.Join(", ", targets.Select(t => $"{t.Run.Target}/{t.Run.Scenario}")));
            }

            var reportId = Path.GetFileNameWithoutExtension(options.OutputPath);
            var html = HtmlReportBuilder.Build(targets, reportId);
            var outPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, html);

            ConsoleLog.Info($"Wrote self-contained HTML report: {outPath} ({html.Length:N0} bytes)");
            return 0;
        }
        catch (ArgumentException ex)
        {
            ConsoleLog.Error(ex.Message);
            ReportOptions.PrintUsage();
            return 64;
        }
        catch (DirectoryNotFoundException ex)
        {
            ConsoleLog.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Unhandled error: {ex.Message}");
            ConsoleLog.Error(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// <c>merge</c> subcommand — aggregate a coordinated multi-host burst campaign's per-host artifacts
    /// into combined per-second conn/s + concurrency and report whether the ≥1,200 / ≥11,000 envelope
    /// was actually reached (test_instruction.md §6.2).
    /// </summary>
    private static int RunMerge(string[] args)
    {
        var options = MergeOptions.Parse(args);
        if (options.ShowHelp)
        {
            MergeOptions.PrintUsage();
            return 0;
        }

        ConsoleLog.Info($"Merging multi-host run results from: {options.InputDir}" +
                        (string.IsNullOrEmpty(options.RunTag) ? "" : $" (tag='{options.RunTag}')"));

        var report = Merger.Merge(options.InputDir, options.RunTag, options.ConcurrentTarget, options.ChurnTarget);
        if (report.Groups.Count == 0)
        {
            ConsoleLog.Warn("No matching run-result JSON files found — nothing to merge.");
        }

        var outPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, report.ToJson());
        ConsoleLog.Info($"Wrote merge summary: {outPath}");

        var csvDir = Path.GetDirectoryName(outPath)!;
        var baseName = Path.GetFileNameWithoutExtension(outPath);
        foreach (var g in report.Groups)
        {
            var safe = $"{g.Target}-{g.Scenario}".Replace(Path.DirectorySeparatorChar, '_');
            var csvPath = Path.Combine(csvDir, $"{baseName}-{safe}-combined.csv");
            Merger.WriteGroupCsv(g, csvPath);

            var churn = g.ReachedChurnTarget ? "REACHED" : "NOT reached";
            var conc = g.ReachedConcurrentTarget ? "REACHED" : "NOT reached";
            ConsoleLog.Info(new string('-', 70));
            ConsoleLog.Info($"{g.Target} / {g.Scenario}: hosts {g.HostsFound}/{g.DeclaredHostCount} " +
                            $"[{string.Join(",", g.HostIds)}]");
            ConsoleLog.Info($"  Combined conn/s peak : {g.PeakCombinedConnPerSec:N0}  (target ≥ {options.ChurnTarget:N0} — {churn})");
            ConsoleLog.Info($"  Combined in-flight peak: {g.PeakCombinedInFlight:N0}  (target ≥ {options.ConcurrentTarget:N0} — {conc})");
            ConsoleLog.Info($"  Combined series CSV   : {csvPath}");
        }

        return 0;
    }
}

/// <summary>Parsed CLI options for
/// <c>merge --input &lt;dir&gt; [--output f.json] [--tag TAG] [--conc-target N] [--churn-target N]</c>.</summary>
public sealed class MergeOptions
{
    public string InputDir { get; private set; } = "results";

    public string OutputPath { get; private set; } = "merge.json";

    public string RunTag { get; private set; } = string.Empty;

    public int ConcurrentTarget { get; private set; } = 11_000;

    public int ChurnTarget { get; private set; } = 1_200;

    public bool ShowHelp { get; private set; }

    public static MergeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new MergeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "merge":
                    break;
                case "--input":
                case "-i":
                    options.InputDir = RequireValue(args, ref i, arg);
                    break;
                case "--output":
                case "-o":
                    options.OutputPath = RequireValue(args, ref i, arg);
                    break;
                case "--tag":
                    options.RunTag = RequireValue(args, ref i, arg);
                    break;
                case "--conc-target":
                    options.ConcurrentTarget = ParsePositive(RequireValue(args, ref i, arg), arg);
                    break;
                case "--churn-target":
                    options.ChurnTarget = ParsePositive(RequireValue(args, ref i, arg), arg);
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    return options;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    private static int ParsePositive(string raw, string flag)
    {
        if (!int.TryParse(raw, out var v) || v <= 0)
        {
            throw new ArgumentException($"{flag} must be a positive integer (got '{raw}').");
        }

        return v;
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
        Console.WriteLine("Usage: merge --input <results-dir> [--output merge.json] [--tag TAG] [--conc-target 11000] [--churn-target 1200]");
        Console.WriteLine();
        Console.WriteLine("  --input,  -i     Directory of per-host run-result JSON files. Default: results.");
        Console.WriteLine("  --output, -o     Output merge-summary JSON path. Default: merge.json.");
        Console.WriteLine("  --tag            Only merge runs whose --run-tag matches TAG (groups one campaign).");
        Console.WriteLine("  --conc-target    Combined concurrent-connection target to test against. Default 11000.");
        Console.WriteLine("  --churn-target   Combined conn/s target to test against. Default 1200.");
        Console.WriteLine("  --help,   -h     Show this help.");
        Console.WriteLine();
        Console.WriteLine("Sums each host's per-second series on the absolute wall-clock second (StartedUnixSeconds +");
        Console.WriteLine("Second) to report combined conn/s and in-flight-concurrency peaks, plus a combined CSV per");
        Console.WriteLine("(target, scenario). Proves whether the ≥1,200 conn/s / ≥11,000 concurrent envelope was reached.");
    }
}

/// <summary>Parsed CLI options for <c>report --input &lt;dir&gt; --output &lt;file.html&gt;</c>.</summary>
public sealed class ReportOptions
{
    public string InputDir { get; private set; } = "results";

    public string OutputPath { get; private set; } = "report.html";

    public bool ShowHelp { get; private set; }

    public static ReportOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ReportOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "report":
                    break;
                case "--input":
                case "-i":
                    options.InputDir = RequireValue(args, ref i, arg);
                    break;
                case "--output":
                case "-o":
                    options.OutputPath = RequireValue(args, ref i, arg);
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    return options;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

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
        Console.WriteLine("Usage: report --input <results-dir> --output <report.html>");
        Console.WriteLine();
        Console.WriteLine("  --input,  -i   Directory of test run-result JSON files (and any preflight JSON). Default: results.");
        Console.WriteLine("  --output, -o   Output HTML file path. Default: report.html.");
        Console.WriteLine("  --help,   -h   Show this help.");
        Console.WriteLine();
        Console.WriteLine("Consumes one or more target result sets and produces a single self-contained HTML report");
        Console.WriteLine("(§8.1): masked conn string, config summary, success/fail, per-second connection + QPS graphs,");
        Console.WriteLine("connection/per-op/total latency graphs, p50/p95/p99/p99.9, error taxonomy, reuse verification,");
        Console.WriteLine("starting-state disclosure, Mongo-VM caveat, and the 3-way comparison + resilience verdict.");
    }
}
