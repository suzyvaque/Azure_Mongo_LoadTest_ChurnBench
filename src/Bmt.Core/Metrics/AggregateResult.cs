using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bmt.Core.Metrics;

/// <summary>
/// Cross-iteration aggregate written as <c>aggregate.json</c> at the campaign folder root after all
/// iterations complete. Contains condensed per-iteration summaries (no bulky time-series) plus
/// cross-iteration mean/min/max statistics for key latency metrics to make stability assessment easy.
/// </summary>
public sealed class AggregateResult
{
    public string Target { get; set; } = string.Empty;

    public string Scenario { get; set; } = string.Empty;

    public string WorkloadMode { get; set; } = string.Empty;

    public string GeneratedUtc { get; set; } = string.Empty;

    public int IterationCount { get; set; }

    public List<IterationSummary> Iterations { get; set; } = new();

    public AggregateStats Stats { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Build the aggregate from a list of per-iteration <see cref="RunResult"/>s and their relative
    /// artifact file paths (for cross-referencing from the aggregate JSON).
    /// </summary>
    public static AggregateResult Build(IReadOnlyList<RunResult> results, IReadOnlyList<string> artifactRelPaths)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new ArgumentException("At least one iteration result is required.", nameof(results));
        }

        var first = results[0];
        var agg = new AggregateResult
        {
            Target = first.Target,
            Scenario = first.Scenario,
            WorkloadMode = first.WorkloadMode,
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            IterationCount = results.Count,
        };

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            agg.Iterations.Add(new IterationSummary
            {
                IterationNumber = r.IterationNumber,
                ArtifactPath = i < artifactRelPaths.Count ? artifactRelPaths[i] : string.Empty,
                DurationSeconds = r.DurationSeconds,
                TotalTasks = r.Totals.TotalTasks,
                SuccessfulTasks = r.Totals.SuccessfulTasks,
                FailedTasks = r.Totals.FailedTasks,
                ErrorRatePct = r.Totals.TotalTasks == 0
                    ? 0
                    : Math.Round(100.0 * r.Totals.FailedTasks / r.Totals.TotalTasks, 3),
                TaskCycleLatencyMs = r.TaskCycleLatencyMs,
                OperationLatencyMs = r.OperationLatencyMs,
                ConnectionOpenMs = r.ConnectionOpenMs,
                ErrorsByType = r.ErrorsByType,
            });
        }

        agg.Stats = AggregateStats.Compute(agg.Iterations);
        return agg;
    }
}

/// <summary>Condensed per-iteration summary included in <see cref="AggregateResult"/> (no time-series).</summary>
public sealed class IterationSummary
{
    public int IterationNumber { get; set; }

    /// <summary>Relative path from the campaign folder to this iteration's JSON artifact.</summary>
    public string ArtifactPath { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public long TotalTasks { get; set; }

    public long SuccessfulTasks { get; set; }

    public long FailedTasks { get; set; }

    public double ErrorRatePct { get; set; }

    public LatencySummary TaskCycleLatencyMs { get; set; } = LatencySummary.Empty();

    public Dictionary<string, LatencySummary> OperationLatencyMs { get; set; } = new();

    public LatencySummary ConnectionOpenMs { get; set; } = LatencySummary.Empty();

    public Dictionary<string, long> ErrorsByType { get; set; } = new();
}

/// <summary>Cross-iteration mean/min/max statistics for key latency metrics.</summary>
public sealed class AggregateStats
{
    /// <summary>Cross-iteration aggregate for the full task-cycle latency.</summary>
    public AggLatency TaskCycleMs { get; set; } = new();

    /// <summary>Cross-iteration aggregate per operation (keyed by op name: find_input / remove / insert / find_output).</summary>
    public Dictionary<string, AggLatency> OperationMs { get; set; } = new();

    /// <summary>Cross-iteration aggregate for connection-open (handshake/auth) latency.</summary>
    public AggLatency ConnectionOpenMs { get; set; } = new();

    /// <summary>Mean throughput (successful tasks/s) across iterations.</summary>
    public double MeanSuccessfulTasksPerSec { get; set; }

    /// <summary>Mean error rate % across iterations.</summary>
    public double MeanErrorRatePct { get; set; }

    public static AggregateStats Compute(IReadOnlyList<IterationSummary> iters)
    {
        if (iters.Count == 0)
        {
            return new AggregateStats();
        }

        var stats = new AggregateStats
        {
            TaskCycleMs = AggLatency.From(iters.Select(i => i.TaskCycleLatencyMs).ToList()),
            ConnectionOpenMs = AggLatency.From(iters.Select(i => i.ConnectionOpenMs).ToList()),
            MeanSuccessfulTasksPerSec = Math.Round(
                iters.Average(i => i.DurationSeconds > 0 ? i.SuccessfulTasks / i.DurationSeconds : 0), 3),
            MeanErrorRatePct = Math.Round(iters.Average(i => i.ErrorRatePct), 3),
        };

        var opNames = iters.SelectMany(i => i.OperationLatencyMs.Keys).Distinct().ToList();
        foreach (var op in opNames)
        {
            var samples = iters
                .Where(i => i.OperationLatencyMs.ContainsKey(op))
                .Select(i => i.OperationLatencyMs[op])
                .ToList();
            stats.OperationMs[op] = AggLatency.From(samples);
        }

        return stats;
    }
}

/// <summary>Cross-iteration aggregate for one latency series (mean/min/max per key percentile).</summary>
public sealed class AggLatency
{
    public double MeanP50Ms { get; set; }

    public double MeanP90Ms { get; set; }

    public double MeanP95Ms { get; set; }

    public double MeanP99Ms { get; set; }

    public double MeanP999Ms { get; set; }

    /// <summary>Best (lowest) p99 observed across iterations.</summary>
    public double MinP99Ms { get; set; }

    /// <summary>Worst (highest) p99 observed across iterations.</summary>
    public double MaxP99Ms { get; set; }

    public long TotalCount { get; set; }

    public static AggLatency From(IReadOnlyList<LatencySummary> samples)
    {
        if (samples.Count == 0)
        {
            return new AggLatency();
        }

        return new AggLatency
        {
            MeanP50Ms = Math.Round(samples.Average(s => s.P50Ms), 3),
            MeanP90Ms = Math.Round(samples.Average(s => s.P90Ms), 3),
            MeanP95Ms = Math.Round(samples.Average(s => s.P95Ms), 3),
            MeanP99Ms = Math.Round(samples.Average(s => s.P99Ms), 3),
            MeanP999Ms = Math.Round(samples.Average(s => s.P999Ms), 3),
            MinP99Ms = samples.Min(s => s.P99Ms),
            MaxP99Ms = samples.Max(s => s.P99Ms),
            TotalCount = samples.Sum(s => s.Count),
        };
    }
}
