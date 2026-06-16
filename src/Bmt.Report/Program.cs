namespace Bmt.Report;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
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
