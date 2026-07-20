using System.Globalization;
using System.Text;
using Bmt.Core.Metrics;

namespace Bmt.Report;

/// <summary>
/// Renders a Markdown summary (<c>summary.md</c>) alongside the HTML report so a campaign no longer has
/// to be summarized by hand. Emits, per target/scenario, the latency percentile table + a churn-resilience
/// verdict (did the run reach the §6.2 ≥1,200 conn/s / ≥11,000 concurrent envelope), and — when more than
/// one target is present for a scenario — a cross-target comparison table with the better value **bolded**,
/// split by steady/burst (test_instruction.md §8; mirrors the hand-written comparison summaries).
/// </summary>
public static class MarkdownSummaryBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>§6.2 combined targets the burst campaign must reach (same defaults as the merge command).</summary>
    private const int ChurnTargetPerSec = 1_200;
    private const int ConcurrentTarget = 11_000;

    public static string Build(IReadOnlyList<LoadedTarget> targets, string? reportId = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var sb = new StringBuilder();
        var idSuffix = string.IsNullOrWhiteSpace(reportId) ? string.Empty : $" — {reportId}";

        sb.Append("# MongoDB Connection-Churn Benchmark — Summary").Append(idSuffix).Append("\n\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"_Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC · {targets.Count} run(s) loaded._\n\n");
        sb.Append("> Warm data cache, cold connections (Model A). Each Task opens a brand-new connection and closes it — ")
          .Append("no pooling/reuse between requests. No pass/fail thresholds: prioritize **p99 (with p95/p99.9)** over the mean.\n\n");

        if (targets.Count == 0)
        {
            sb.Append("No run-result JSON files were found in the input directory. Run `test --target <key>` first, ")
              .Append("then point `report --input` at `results/`.\n");
            return sb.ToString();
        }

        AppendComparison(sb, targets);

        sb.Append("## Per-target detail\n\n");
        foreach (var t in targets)
        {
            AppendTargetDetail(sb, t);
        }

        return sb.ToString();
    }

    private static void AppendComparison(StringBuilder sb, IReadOnlyList<LoadedTarget> targets)
    {
        // Compare DIFFERENT targets under the SAME scenario (steady vs burst kept separate).
        var scenarioGroups = targets
            .GroupBy(t => t.Run.Scenario, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(t => t.Run.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();

        if (scenarioGroups.Count == 0)
        {
            return;
        }

        sb.Append("## Cross-target comparison\n\n");
        sb.Append("Latency in ms; **better value bolded** (lower latency / higher throughput / lower error rate).\n\n");

        foreach (var group in scenarioGroups)
        {
            var runs = group
                .OrderBy(t => t.Run.Target, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.Run)
                .ToList();

            sb.Append(CultureInfo.InvariantCulture, $"### {Title(group.Key)}\n\n");

            // Header row.
            sb.Append("| Metric | Pctile |");
            foreach (var r in runs)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {r.Target} |");
            }

            sb.Append('\n').Append("|---|---|");
            foreach (var _ in runs)
            {
                sb.Append("---|");
            }

            sb.Append('\n');

            // Headline: throughput (higher better) + error rate (lower better).
            Row(sb, "Throughput (tasks/s)", "—",
                runs.Select(Throughput).ToList(), higherIsBetter: true, "F1");
            Row(sb, "Error rate", "—",
                runs.Select(ErrorRatePercent).ToList(), higherIsBetter: false, "P2");

            // Connection open (TCP+TLS+auth).
            LatencyRows(sb, "Connection (open)", runs.Select(r => r.ConnectionOpenMs).ToList());
            // Full per-Task cycle.
            LatencyRows(sb, "Total cycle", runs.Select(r => r.TaskCycleLatencyMs).ToList());

            sb.Append('\n');
        }
    }

    private static void LatencyRows(StringBuilder sb, string label, IReadOnlyList<LatencySummary> series)
    {
        Row(sb, label, "p50", series.Select(s => s.P50Ms).ToList(), higherIsBetter: false, "F1");
        Row(sb, "", "p95", series.Select(s => s.P95Ms).ToList(), higherIsBetter: false, "F1");
        Row(sb, "", "p99", series.Select(s => s.P99Ms).ToList(), higherIsBetter: false, "F1");
        Row(sb, "", "p99.9", series.Select(s => s.P999Ms).ToList(), higherIsBetter: false, "F1");
    }

    private static void Row(
        StringBuilder sb, string metric, string pctile, IReadOnlyList<double> values, bool higherIsBetter, string fmt)
    {
        var bestIdx = BestIndex(values, higherIsBetter);
        sb.Append(CultureInfo.InvariantCulture, $"| {metric} | {pctile} |");
        for (var i = 0; i < values.Count; i++)
        {
            var cell = fmt == "P2"
                ? (values[i] / 100.0).ToString("P2", Inv)
                : values[i].ToString(fmt, Inv);
            sb.Append(i == bestIdx ? $" **{cell}** |" : $" {cell} |");
        }

        sb.Append('\n');
    }

    /// <summary>Index of the best value (min or max); -1 when all values tie so nothing is bolded.</summary>
    private static int BestIndex(IReadOnlyList<double> values, bool higherIsBetter)
    {
        if (values.Count < 2)
        {
            return -1;
        }

        var best = higherIsBetter ? double.NegativeInfinity : double.PositiveInfinity;
        var idx = -1;
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (higherIsBetter ? v > best : v < best)
            {
                best = v;
                idx = i;
            }
        }

        // No unique winner (all equal) → don't bold.
        return values.All(v => Math.Abs(v - values[0]) < 1e-9) ? -1 : idx;
    }

    private static void AppendTargetDetail(StringBuilder sb, LoadedTarget t)
    {
        var r = t.Run;
        sb.Append(CultureInfo.InvariantCulture, $"### {r.Target} — {Title(r.Scenario)}\n\n");

        var succ = r.Totals.TotalTasks == 0 ? 0 : 100.0 * r.Totals.SuccessfulTasks / r.Totals.TotalTasks;
        var peakConnPerSec = r.Throughput.Count == 0 ? 0 : r.Throughput.Max(p => p.ConnectionsCreated);
        var peakInFlight = r.Throughput.Count == 0 ? 0 : r.Throughput.Max(p => p.InFlightTasks);

        sb.Append(CultureInfo.InvariantCulture, $"- Window: {r.StartedUtc} → {r.FinishedUtc} ({r.DurationSeconds.ToString("F1", Inv)} s)\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Tasks (ok/fail): {r.Totals.SuccessfulTasks:N0} / {r.Totals.FailedTasks:N0} — success {succ.ToString("F2", Inv)}%\n");
        sb.Append(CultureInfo.InvariantCulture, $"- No-reuse confirmed: {(r.ReuseCheck.NoReuseConfirmed ? "yes" : "**NO**")} (created {r.Connections.Created:N0} / closed {r.Connections.Closed:N0} vs {r.Totals.TotalTasks:N0} Tasks)\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Peak conn/s: {peakConnPerSec:N0} · Peak in-flight: {peakInFlight:N0}\n\n");

        AppendChurnVerdict(sb, peakConnPerSec, peakInFlight, r.HostCount);

        // Latency percentile table.
        sb.Append("| Series | Count | Min | Mean | p50 | p90 | p95 | p99 | p99.9 | Max |\n");
        sb.Append("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var op in OpNames.Ordered)
        {
            if (r.OperationLatencyMs.TryGetValue(op, out var s))
            {
                LatencyTableRow(sb, OpLabel(op), s);
            }
        }

        LatencyTableRow(sb, "full cycle", r.TaskCycleLatencyMs);
        LatencyTableRow(sb, "connection open", r.ConnectionOpenMs);
        LatencyTableRow(sb, "handshake hello", r.HandshakeHelloMs);
        LatencyTableRow(sb, "handshake auth (SCRAM)", r.HandshakeAuthMs);
        sb.Append('\n');

        AppendErrors(sb, r);
    }

    private static void AppendChurnVerdict(StringBuilder sb, long peakConnPerSec, long peakInFlight, int hostCount)
    {
        var churnOk = peakConnPerSec >= ChurnTargetPerSec;
        var concOk = peakInFlight >= ConcurrentTarget;
        var churn = churnOk ? "REACHED" : "not reached";
        var conc = concOk ? "REACHED" : "not reached";
        var scope = hostCount > 1 ? $" (single host of {hostCount}; use `merge` for the combined envelope)" : string.Empty;

        sb.Append(CultureInfo.InvariantCulture,
            $"> **Churn-resilience verdict{scope}:** conn/s {peakConnPerSec:N0} vs ≥{ChurnTargetPerSec:N0} target — {churn}; " +
            $"in-flight {peakInFlight:N0} vs ≥{ConcurrentTarget:N0} target — {conc}.\n\n");
    }

    private static void AppendErrors(StringBuilder sb, RunResult r)
    {
        if (r.ErrorsByType.Count == 0)
        {
            sb.Append("_No errors recorded._\n\n");
            return;
        }

        sb.Append("Errors by type: ");
        sb.Append(string.Join(", ", r.ErrorsByType
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"`{kv.Key}` {kv.Value:N0}")));
        sb.Append("\n\n");
    }

    private static void LatencyTableRow(StringBuilder sb, string name, LatencySummary s) =>
        sb.Append(CultureInfo.InvariantCulture,
            $"| {name} | {s.Count:N0} | {s.MinMs.ToString("F2", Inv)} | {s.MeanMs.ToString("F2", Inv)} | " +
            $"{s.P50Ms.ToString("F2", Inv)} | {s.P90Ms.ToString("F2", Inv)} | {s.P95Ms.ToString("F2", Inv)} | " +
            $"{s.P99Ms.ToString("F2", Inv)} | {s.P999Ms.ToString("F2", Inv)} | {s.MaxMs.ToString("F2", Inv)} |\n");

    private static double Throughput(RunResult r) =>
        r.DurationSeconds <= 0 ? 0 : r.Totals.SuccessfulTasks / r.DurationSeconds;

    private static double ErrorRatePercent(RunResult r) =>
        r.Totals.TotalTasks == 0 ? 0 : 100.0 * r.Totals.FailedTasks / r.Totals.TotalTasks;

    private static string OpLabel(string op) => op switch
    {
        OpNames.FindInput => "find input",
        OpNames.Remove => "remove",
        OpNames.Insert => "insert",
        OpNames.FindOutput => "find output",
        _ => op,
    };

    private static string Title(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Inv) + s[1..];
}
