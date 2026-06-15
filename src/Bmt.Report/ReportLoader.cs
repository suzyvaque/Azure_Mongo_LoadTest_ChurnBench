using System.Text.Json;
using Bmt.Core.Metrics;

namespace Bmt.Report;

/// <summary>One target's loaded artifacts: its run result plus the matching preflight summary (if any).</summary>
public sealed class LoadedTarget
{
    public required RunResult Run { get; init; }

    public PreflightSummary? Preflight { get; init; }

    public string SourceFile { get; init; } = string.Empty;
}

/// <summary>
/// Scans an input directory for <c>test</c> run-result JSON files (and any sibling preflight JSON
/// artifacts) and projects them into <see cref="LoadedTarget"/>s for the HTML report. When several run
/// results exist for one target, the most recent (by FinishedUtc) wins. Preflight summaries are matched
/// to runs by target key.
/// </summary>
public static class ReportLoader
{
    public static IReadOnlyList<LoadedTarget> Load(string inputDir)
    {
        if (!Directory.Exists(inputDir))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {inputDir}");
        }

        var runs = new List<(RunResult Run, string File)>();
        var preflights = new List<PreflightSummary>();

        foreach (var path in Directory.EnumerateFiles(inputDir, "*.json", SearchOption.AllDirectories))
        {
            string json;
            JsonElement root;
            try
            {
                json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                root = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn($"Skipping unreadable JSON '{Path.GetFileName(path)}': {ex.Message}");
                continue;
            }

            if (PreflightSummary.LooksLikePreflight(root))
            {
                try
                {
                    preflights.Add(PreflightSummary.FromJson(json));
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn($"Skipping malformed preflight '{Path.GetFileName(path)}': {ex.Message}");
                }

                continue;
            }

            if (LooksLikeRun(root))
            {
                try
                {
                    runs.Add((RunResult.FromJson(json), path));
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn($"Skipping malformed run result '{Path.GetFileName(path)}': {ex.Message}");
                }
            }
        }

        // Most-recent run per (target, scenario); newest preflight per target.
        var latestPreflight = preflights
            .GroupBy(p => p.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.TimestampUtc).First(), StringComparer.OrdinalIgnoreCase);

        var result = runs
            .GroupBy(r => $"{r.Run.Target}|{r.Run.Scenario}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Run.FinishedUtc).First())
            .OrderBy(r => r.Run.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Run.Scenario, StringComparer.OrdinalIgnoreCase)
            .Select(r => new LoadedTarget
            {
                Run = r.Run,
                Preflight = latestPreflight.GetValueOrDefault(r.Run.Target),
                SourceFile = Path.GetFileName(r.File),
            })
            .ToList();

        return result;
    }

    private static bool LooksLikeRun(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Totals", out _) &&
        root.TryGetProperty("OperationLatencyMs", out _);
}
