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

            // Also emit a Markdown summary (per-target percentiles + churn verdict + cross-target
            // comparison) so a campaign no longer has to be summarized by hand.
            var markdown = MarkdownSummaryBuilder.Build(targets, reportId);
            var mdPath = Path.ChangeExtension(outPath, ".md");
            File.WriteAllText(mdPath, markdown);
            ConsoleLog.Info($"Wrote Markdown summary: {mdPath} ({markdown.Length:N0} bytes)");
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
            var safe = $"{g.Target}-{g.Scenario}-iter{g.IterationNumber:D2}".Replace(Path.DirectorySeparatorChar, '_');
            var csvPath = Path.Combine(csvDir, $"{baseName}-{safe}-combined.csv");
            Merger.WriteGroupCsv(g, csvPath);

            var churn = g.ReachedChurnTarget ? "REACHED" : "NOT reached";
            ConsoleLog.Info(new string('-', 70));
            ConsoleLog.Info($"{g.Target} / {g.Scenario} / iter {g.IterationNumber}: hosts {g.HostsFound}/{g.DeclaredHostCount} " +
                            $"[{string.Join(",", g.HostIds)}]  " +
                            (g.Valid ? "VALID" : "INVALID (" +
                                string.Join(" ", new[]
                                {
                                    g.MissingHostIds.Count > 0 ? $"missing=[{string.Join(",", g.MissingHostIds)}]" : null,
                                    g.UnexpectedHostIds.Count > 0 ? $"unexpected=[{string.Join(",", g.UnexpectedHostIds)}]" : null,
                                    !g.HostCountConsistent ? "inconsistent-host-count" : null,
                                }.Where(x => x != null)) + ")"));
            if (g.SupersededRuns > 0)
            {
                ConsoleLog.Info($"  Retries superseded    : {g.SupersededRuns} older-attempt artifact(s) for host(s) [{string.Join(",", g.RetriedHostIds)}] (latest attempt used)");
            }
            ConsoleLog.Info($"  Start-time skew       : {g.StartSkewSeconds}s across hosts");
            ConsoleLog.Info($"  Offered/started/s     : {g.CombinedOfferedTasksPerSec:N0} / {g.CombinedStartedTasksPerSec:N0}  " +
                            $"(failure {g.FailureRatePct:F2}%)");
            ConsoleLog.Info($"  Combined conn/s peak  : {g.PeakCombinedConnPerSec:N0}  (target ≥ {options.ChurnTarget:N0} — {churn})");
            ConsoleLog.Info($"  Combined ready/s peak : {g.PeakCombinedReadyPerSec:N0}  (target ≥ {options.ChurnTarget:N0} — {(g.ReachedReadyChurnTarget ? "REACHED" : "NOT reached")})");
            ConsoleLog.Info($"  Combined active-ready  : {g.PeakCombinedActiveReady:N0}  (AUTHORITATIVE concurrency; target ≥ {options.ConcurrentTarget:N0} — {(g.ReachedConcurrentTarget ? "REACHED" : "NOT reached")})");
            ConsoleLog.Info($"  Combined in-flight peak: {g.PeakCombinedInFlight:N0}  (generator diagnostic only — NOT connection proof)");
            ConsoleLog.Info($"  True e2e p99 / drain  : {g.TrueE2eP99Ms:N1} ms / {g.DrainDurationSeconds:F1}s (worst host)  reconciled={g.AllHostsReconciled}");
            ConsoleLog.Info($"  Combined series CSV   : {csvPath}");
        }

        foreach (var s in report.CrossIteration.Stats)
        {
            ConsoleLog.Info(new string('=', 70));
            ConsoleLog.Info($"CROSS-ITERATION {s.Target} / {s.Scenario}: {s.ValidIterations}/{s.ExpectedIterations} valid iterations" +
                            (s.InvalidIterationNumbers.Count > 0 ? $" (invalid/missing: {string.Join(",", s.InvalidIterationNumbers)})" : ""));
            if (s.MissingIterationNumbers.Count > 0)
            {
                ConsoleLog.Warn($"  Entirely-missing iterations: [{string.Join(",", s.MissingIterationNumbers)}] — no host artifacts found.");
            }
            if (s.ValidIterations > 0)
            {
                ConsoleLog.Info($"  Peak conn/s    mean={s.MeanPeakConnPerSec:N0} min={s.MinPeakConnPerSec:N0} max={s.MaxPeakConnPerSec:N0}");
                ConsoleLog.Info($"  Peak ready/s   mean={s.MeanPeakReadyPerSec:N0} min={s.MinPeakReadyPerSec:N0} max={s.MaxPeakReadyPerSec:N0}");
                ConsoleLog.Info($"  Peak active-rdy mean={s.MeanPeakActiveReady:N0} min={s.MinPeakActiveReady:N0} max={s.MaxPeakActiveReady:N0}  (authoritative concurrency)");
                ConsoleLog.Info($"  Peak in-flight mean={s.MeanPeakInFlight:N0} min={s.MinPeakInFlight:N0} max={s.MaxPeakInFlight:N0}  (diagnostic)");
                ConsoleLog.Info($"  True e2e p99   mean={s.MeanTrueE2eP99Ms:N1} min={s.MinTrueE2eP99Ms:N1} max={s.MaxTrueE2eP99Ms:N1} ms");
                ConsoleLog.Info($"  Drain seconds  mean={s.MeanDrainDurationSeconds:F1} min={s.MinDrainDurationSeconds:F1} max={s.MaxDrainDurationSeconds:F1}   Failure% mean={s.MeanFailureRatePct:F2} min={s.MinFailureRatePct:F2} max={s.MaxFailureRatePct:F2}");
                ConsoleLog.Info($"  All iters reached: churn={s.AllIterationsReachedChurn} active-ready={s.AllIterationsReachedActiveReady}");
            }
            ConsoleLog.Info($"  Max start-time skew   : {s.MaxStartSkewSeconds}s");
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
        Console.WriteLine("A Markdown summary (same base name, .md) with the cross-target comparison is written alongside.");
    }
}
