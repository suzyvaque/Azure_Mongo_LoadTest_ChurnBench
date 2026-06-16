using System.Globalization;
using System.Text;
using Bmt.Core.Metrics;
using Bmt.Report.Charts;

namespace Bmt.Report;

/// <summary>
/// Renders the self-contained HTML report (test_instruction.md §8.1) from the loaded run results +
/// preflight summaries. No external dependencies — all CSS and SVG charts are inlined so the file opens
/// locally with no server. Includes the mandatory starting-state disclosure, the Mongo-VM caveat box,
/// the unique-vs-non-unique ReqId-index divergence display, connection-reuse verification, and (when
/// multiple targets are present) the 3-way comparison + churn-resilience verdict.
/// </summary>
public static class HtmlReportBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Operation colors (find input / remove / insert / find output / combined).
    private const string CFindInput = "#2563eb";
    private const string CRemove = "#dc2626";
    private const string CInsert = "#16a34a";
    private const string CFindOutput = "#9333ea";
    private const string CCombined = "#0f172a";

    // Percentile colors (p50 / p95 / p99 / p99.9).
    private const string CP50 = "#60a5fa";
    private const string CP95 = "#f59e0b";
    private const string CP99 = "#ef4444";
    private const string CP999 = "#7c3aed";

    public static string Build(IReadOnlyList<LoadedTarget> targets, string? reportId = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var hasId = !string.IsNullOrWhiteSpace(reportId);
        var titleSuffix = hasId ? $" \u2014 {reportId}" : string.Empty;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append(CultureInfo.InvariantCulture, $"<title>MongoDB Connection-Churn Benchmark Report{(hasId ? " - " + Svg.Esc(reportId!) : string.Empty)}</title>");
        sb.Append("<style>").Append(Css()).Append("</style></head><body>");

        sb.Append(CultureInfo.InvariantCulture, $"<h1>MongoDB Connection-Churn Benchmark Report{(hasId ? " &mdash; <span class=\"rid\">" + Svg.Esc(reportId!) + "</span>" : string.Empty)}</h1>");
        sb.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC &middot; {targets.Count} run(s) loaded.{(hasId ? " &middot; report id <code>" + Svg.Esc(reportId!) + "</code>" : string.Empty)}</p>");

        AppendDisclosure(sb);

        if (targets.Count == 0)
        {
            sb.Append("<p class=\"warn\">No run-result JSON files found in the input directory. " +
                      "Run <code>test --target &lt;key&gt;</code> first, then point <code>report --input</code> at results/.</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        if (targets.Count > 1)
        {
            AppendComparison(sb, targets);
        }

        foreach (var t in targets)
        {
            AppendTargetSection(sb, t);
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendDisclosure(StringBuilder sb)
    {
        sb.Append("<div class=\"box info\"><h3>Starting-state disclosure (Model A, warm cache, cold connections)</h3>");
        sb.Append("<p>Results reflect a <strong>warm data cache</strong> (untimed pre-read, no retained connections) and a " +
                  "<strong>cold connection state</strong> under <strong>Model A (immutable input)</strong>. Each Task opens a " +
                  "brand-new connection and closes it (no pooling/reuse between requests). An internal driver pool object may " +
                  "exist, but no pooling/reuse occurs between requests.</p></div>");

        sb.Append("<div class=\"box caveat\"><h3>Mongo-VM caveat</h3>");
        sb.Append("<p>For <code>mongo-vm</code>, two real-world effects are <strong>not</strong> exercised and may make production " +
                  "behavior worse than these steady-warm numbers: (a) <strong>post-load cold start</strong> — the first wave of " +
                  "Tasks right after the daily load hits a partially cold <code>calc_input</code>; and (b) <strong>mid-day input " +
                  "updates (Model B append-only / Model C mutable-in-place)</strong>. Managed services are effectively always warm, " +
                  "so the warm-cache comparison is fair for most of the day but does not capture Mongo's daily cold edge.</p></div>");

        sb.Append("<div class=\"box\"><p class=\"muted\">No pass/fail thresholds — this is a comparison study. Prioritize " +
                  "<strong>p99 (with p95/p99.9)</strong> over the average. This benchmark does <strong>not</strong> represent " +
                  "typical long-lived connection-pool application performance.</p></div>");
    }

    private static void AppendComparison(StringBuilder sb, IReadOnlyList<LoadedTarget> targets)
    {
        sb.Append("<h2>Three-way comparison</h2>");
        sb.Append("<table><thead><tr><th>Target</th><th>Scenario</th><th>Tasks (ok/fail)</th><th>Success %</th>" +
                  "<th>Cycle p99 (ms)</th><th>Cycle p99.9 (ms)</th><th>Throttling</th><th>Port exhaustion</th><th>No-reuse</th></tr></thead><tbody>");

        foreach (var t in targets)
        {
            var r = t.Run;
            var succ = r.Totals.TotalTasks == 0 ? 0 : 100.0 * r.Totals.SuccessfulTasks / r.Totals.TotalTasks;
            var throttle = r.ErrorsByType.GetValueOrDefault("CosmosRuThrottling", 0) + r.ErrorsByType.GetValueOrDefault("ThrottlingOrRateLimit", 0);
            var ports = r.ErrorsByType.GetValueOrDefault("ClientPortExhaustion", 0);
            sb.Append(CultureInfo.InvariantCulture,
                $"<tr><td><strong>{Svg.Esc(r.Target)}</strong></td><td>{Svg.Esc(r.Scenario)}</td>" +
                $"<td>{r.Totals.SuccessfulTasks:N0}/{r.Totals.FailedTasks:N0}</td><td>{succ.ToString("F2", Inv)}</td>" +
                $"<td>{r.TaskCycleLatencyMs.P99Ms.ToString("F1", Inv)}</td><td>{r.TaskCycleLatencyMs.P999Ms.ToString("F1", Inv)}</td>" +
                $"<td>{throttle:N0}</td><td>{ports:N0}</td><td>{(r.ReuseCheck.NoReuseConfirmed ? "yes" : "NO")}</td></tr>");
        }

        sb.Append("</tbody></table>");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"box verdict\"><h3>Churn-resilience verdict</h3>{ResilienceVerdict(targets)}</div>");
    }

    private static string ResilienceVerdict(IReadOnlyList<LoadedTarget> targets)
    {
        // Heuristic: most resilient = highest success rate, then lowest cycle p99, penalizing throttling and
        // port exhaustion. This is guidance, not a pass/fail — the operator should read the per-target detail.
        LoadedTarget? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var t in targets)
        {
            var r = t.Run;
            var succ = r.Totals.TotalTasks == 0 ? 0 : (double)r.Totals.SuccessfulTasks / r.Totals.TotalTasks;
            var p99 = r.TaskCycleLatencyMs.P99Ms <= 0 ? 1 : r.TaskCycleLatencyMs.P99Ms;
            var throttle = r.ErrorsByType.GetValueOrDefault("CosmosRuThrottling", 0) + r.ErrorsByType.GetValueOrDefault("ThrottlingOrRateLimit", 0);
            var score = succ * 1000.0 - Math.Log10(p99) * 10.0 - throttle * 0.001;
            if (score > bestScore)
            {
                bestScore = score;
                best = t;
            }
        }

        if (best is null)
        {
            return "<p>Insufficient data for a verdict.</p>";
        }

        var b = best.Run;
        return $"<p>On the loaded runs, <strong>{Svg.Esc(b.Target)}</strong> looks most resilient to connection churn " +
               $"(success {(b.Totals.TotalTasks == 0 ? 0 : 100.0 * b.Totals.SuccessfulTasks / b.Totals.TotalTasks).ToString("F2", Inv)}%, " +
               $"cycle p99 {b.TaskCycleLatencyMs.P99Ms.ToString("F1", Inv)} ms). " +
               "Weigh this against the Mongo-VM caveat (cold-start / mid-day updates not modeled) and any Cosmos RU throttling " +
               "or client-side port exhaustion noted per target. This is guidance, not a pass/fail.</p>";
    }

    private static void AppendTargetSection(StringBuilder sb, LoadedTarget t)
    {
        var r = t.Run;
        sb.Append(CultureInfo.InvariantCulture, $"<h2 id=\"{Svg.Esc(r.Target)}-{Svg.Esc(r.Scenario)}\">{Svg.Esc(r.Target)} &mdash; {Svg.Esc(r.Scenario)}</h2>");

        // Summary + config.
        sb.Append("<div class=\"grid2\">");
        sb.Append("<table class=\"kv\"><tbody>");
        Kv(sb, "Target", r.Target);
        Kv(sb, "Scenario", r.Scenario);
        Kv(sb, "Connection string (masked)", r.MaskedConnectionString);
        Kv(sb, "Started (UTC)", r.StartedUtc);
        Kv(sb, "Finished (UTC)", r.FinishedUtc);
        Kv(sb, "Duration (s)", r.DurationSeconds.ToString("F1", Inv));
        Kv(sb, "taskSleepMs", r.TaskSleepMs.ToString(Inv));
        Kv(sb, "Dataset documents (calc_input)", r.DatasetDocumentCount.ToString("N0", Inv));
        Kv(sb, "Total Tasks", r.Totals.TotalTasks.ToString("N0", Inv));
        Kv(sb, "Successful / Failed Tasks", $"{r.Totals.SuccessfulTasks:N0} / {r.Totals.FailedTasks:N0}");
        Kv(sb, "Total Ops / Failed Ops", $"{r.Totals.TotalOps:N0} / {r.Totals.FailedOps:N0}");
        sb.Append("</tbody></table>");

        AppendConfigSummary(sb, t);
        sb.Append("</div>");

        AppendReuseBox(sb, r);

        // Graphs.
        sb.Append("<div class=\"charts\">");
        sb.Append(ConnectionsCreatedChart(r));
        sb.Append(ConnectionsClosedChart(r));
        sb.Append(QpsChart(r));
        sb.Append(ConnectionLatencyChart(r));
        sb.Append(PerOpLatencyChart(r));
        sb.Append(CycleLatencyChart(r));
        sb.Append(ResourceChart(r));
        sb.Append("</div>");

        AppendPercentileTable(sb, r);
        AppendErrorTable(sb, r);
        AppendResourcePeaks(sb, r);
    }

    private static void AppendConfigSummary(StringBuilder sb, LoadedTarget t)
    {
        var r = t.Run;
        var p = t.Preflight;
        sb.Append("<table class=\"kv\"><tbody>");
        sb.Append("<tr><th colspan=\"2\">Preflight / server configuration (§6.3 check 6)</th></tr>");
        if (p is null)
        {
            Kv(sb, "Preflight artifact", "not found in input (run preflight to record server config & index policy)");
        }
        else
        {
            Kv(sb, "Preflight outcome", p.Outcome);
            Kv(sb, "Server version", p.ServerConfig.ServerVersion ?? "n/a");
            if (p.ServerConfig.CosmosExpectedRuPerSec is { } ru)
            {
                Kv(sb, "Cosmos provisioned RU/s (fixed)", ru.ToString("N0", Inv));
            }

            if (!string.IsNullOrEmpty(p.ServerConfig.DocumentDbExpectedTier))
            {
                Kv(sb, "DocumentDB tier", p.ServerConfig.DocumentDbExpectedTier!);
            }

            if (p.ServerConfig.MongoLiveConnectionCeiling is { } ceil)
            {
                Kv(sb, "mongo-vm live connection ceiling", ceil.ToString("N0", Inv));
            }

            Kv(sb, "Host churn capacity (conn/s)", p.HostHeadroom.ChurnCapacityPerSec.ToString("N0", Inv));
            Kv(sb, "Ephemeral ports / TIME_WAIT delay", $"{p.HostHeadroom.EphemeralPortCount:N0} ports / {p.HostHeadroom.TcpTimedWaitDelaySeconds}s");
        }

        // ReqId index policy + divergence (from run gate; enriched by preflight when present).
        var unique = p?.IndexPolicy.InputIndexUnique ?? r.Preflight.InputIndexUnique;
        var diverges = p?.IndexPolicy.UniquenessDivergesFromCanonical ?? r.Preflight.IndexUniquenessDiverges;
        var guarantee = !string.IsNullOrEmpty(p?.IndexPolicy.DistinctReqIdGuarantee)
            ? p!.IndexPolicy.DistinctReqIdGuarantee
            : r.Preflight.DistinctReqIdGuarantee;
        Kv(sb, "calc_input ReqId index", unique ? "unique" : "NON-UNIQUE");
        if (diverges)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<tr><td>Index uniqueness divergence</td><td class=\"diverge\">cosmos-ru ReqId index is non-unique (platform constraint). {Svg.Esc(guarantee)}</td></tr>");
        }

        sb.Append("</tbody></table>");
    }

    private static void AppendReuseBox(StringBuilder sb, RunResult r)
    {
        var cls = r.ReuseCheck.NoReuseConfirmed ? "ok" : "warn";
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"box {cls}\"><h3>Connection-reuse verification</h3>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<p><strong>No-reuse confirmed: {(r.ReuseCheck.NoReuseConfirmed ? "YES" : "NO")}</strong> " +
            $"(created {r.Connections.Created:N0}, closed {r.Connections.Closed:N0}, ready {r.Connections.Ready:N0} vs " +
            $"{r.Totals.TotalTasks:N0} Tasks; created/Task {r.Connections.CreatedToTaskRatio.ToString("F3", Inv)}). " +
            $"{Svg.Esc(r.ReuseCheck.Detail)}</p>");
        sb.Append("<p class=\"muted\">An internal driver pool object may exist, but no pooling/reuse occurs between requests.</p></div>");
    }

    private static string ConnectionsCreatedChart(RunResult r)
    {
        var pts = r.Throughput.Select(p => ((double)p.Second, (double)p.ConnectionsCreated)).ToList();
        return Svg.LineChart("Connections created / second", "elapsed (s)", "conn/s",
            new[] { new LineSeries("created", CInsert, pts) });
    }

    private static string ConnectionsClosedChart(RunResult r)
    {
        var pts = r.Throughput.Select(p => ((double)p.Second, (double)p.ConnectionsClosed)).ToList();
        return Svg.LineChart("Connections closed / second", "elapsed (s)", "conn/s",
            new[] { new LineSeries("closed", CRemove, pts) });
    }

    private static string QpsChart(RunResult r)
    {
        LineSeries S(string name, string color, Func<ThroughputPoint, long> sel) =>
            new(name, color, r.Throughput.Select(p => ((double)p.Second, (double)sel(p))).ToList());

        return Svg.LineChart("Ops QPS / second (per op type + combined)", "elapsed (s)", "ops/s", new[]
        {
            S("find input", CFindInput, p => p.FindInputOps),
            S("remove", CRemove, p => p.RemoveOps),
            S("insert", CInsert, p => p.InsertOps),
            S("find output", CFindOutput, p => p.FindOutputOps),
            S("combined", CCombined, p => p.CombinedOps),
        });
    }

    private static string ConnectionLatencyChart(RunResult r)
    {
        // No per-second connection-latency series is collected; show the open-latency percentile profile.
        var s = r.ConnectionOpenMs;
        var group = new BarGroup("connection open", PercentileBars(s));
        return Svg.GroupedBarChart("Connection latency (open handshake/TLS/auth) percentiles", "ms",
            new[] { group }, PercentileLegend());
    }

    private static string PerOpLatencyChart(RunResult r)
    {
        var groups = new List<BarGroup>();
        foreach (var op in OpNames.Ordered)
        {
            if (r.OperationLatencyMs.TryGetValue(op, out var s))
            {
                groups.Add(new BarGroup(OpLabel(op), PercentileBars(s)));
            }
        }

        return Svg.GroupedBarChart("Per-operation latency percentiles", "ms", groups, PercentileLegend());
    }

    private static string CycleLatencyChart(RunResult r)
    {
        var group = new BarGroup("full cycle", PercentileBars(r.TaskCycleLatencyMs));
        return Svg.GroupedBarChart("Total latency — full per-Task cycle (incl. taskSleepMs)", "ms",
            new[] { group }, PercentileLegend());
    }

    private static string ResourceChart(RunResult r)
    {
        if (r.ResourceSamples.Count == 0)
        {
            return string.Empty;
        }

        LineSeries S(string name, string color, Func<ResourceSample, double> sel) =>
            new(name, color, r.ResourceSamples.Select(p => ((double)p.Second, sel(p))).ToList());

        return Svg.LineChart("Client host resources (§7.3)", "elapsed (s)", "count", new[]
        {
            S("ephemeral ports", CFindInput, x => x.EphemeralPortsInUse),
            S("TIME_WAIT", CRemove, x => x.TimeWaitSockets),
            S("handles", CInsert, x => x.HandleCount),
        });
    }

    private static IReadOnlyList<BarValue> PercentileBars(LatencySummary s) => new[]
    {
        new BarValue("p50", CP50, s.P50Ms),
        new BarValue("p95", CP95, s.P95Ms),
        new BarValue("p99", CP99, s.P99Ms),
        new BarValue("p99.9", CP999, s.P999Ms),
    };

    private static IReadOnlyList<(string, string)> PercentileLegend() => new[]
    {
        ("p50", CP50), ("p95", CP95), ("p99", CP99), ("p99.9", CP999),
    };

    private static void AppendPercentileTable(StringBuilder sb, RunResult r)
    {
        sb.Append("<h3>Latency summary (p50 / p95 / p99 / p99.9)</h3>");
        sb.Append("<table><thead><tr><th>Series</th><th>Count</th><th>Min</th><th>Mean</th><th>p50</th><th>p90</th>" +
                  "<th>p95</th><th>p99</th><th>p99.9</th><th>Max</th></tr></thead><tbody>");
        foreach (var op in OpNames.Ordered)
        {
            if (r.OperationLatencyMs.TryGetValue(op, out var s))
            {
                LatencyRow(sb, OpLabel(op), s);
            }
        }

        LatencyRow(sb, "full cycle", r.TaskCycleLatencyMs);
        LatencyRow(sb, "connection open", r.ConnectionOpenMs);
        LatencyRow(sb, "client create", r.ClientCreateMs);
        sb.Append("</tbody></table>");
    }

    private static void LatencyRow(StringBuilder sb, string name, LatencySummary s)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<tr><td>{Svg.Esc(name)}</td><td>{s.Count:N0}</td><td>{s.MinMs.ToString("F2", Inv)}</td>" +
            $"<td>{s.MeanMs.ToString("F2", Inv)}</td><td>{s.P50Ms.ToString("F2", Inv)}</td><td>{s.P90Ms.ToString("F2", Inv)}</td>" +
            $"<td>{s.P95Ms.ToString("F2", Inv)}</td><td class=\"hi\">{s.P99Ms.ToString("F2", Inv)}</td>" +
            $"<td class=\"hi\">{s.P999Ms.ToString("F2", Inv)}</td><td>{s.MaxMs.ToString("F2", Inv)}</td></tr>");
    }

    private static void AppendErrorTable(StringBuilder sb, RunResult r)
    {
        sb.Append("<h3>Error counts by type (§7.4)</h3>");
        if (r.ErrorsByType.Count == 0)
        {
            sb.Append("<p class=\"muted\">No errors recorded.</p>");
            return;
        }

        sb.Append("<table><thead><tr><th>Error type</th><th>Count</th></tr></thead><tbody>");
        foreach (var kv in r.ErrorsByType.OrderByDescending(kv => kv.Value))
        {
            var cls = kv.Key is "ClientPortExhaustion" ? " class=\"client-err\"" : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"<tr{cls}><td>{Svg.Esc(kv.Key)}</td><td>{kv.Value:N0}</td></tr>");
        }

        sb.Append("</tbody></table>");
        sb.Append("<p class=\"muted\">Client-side limits (e.g. <code>ClientPortExhaustion</code>) are reported separately from " +
                  "server errors so they are not misattributed to the database (§7.3).</p>");
    }

    private static void AppendResourcePeaks(StringBuilder sb, RunResult r)
    {
        var p = r.Process;
        sb.Append("<h3>Client host peaks (§7.3)</h3><table class=\"kv\"><tbody>");
        Kv(sb, "Peak ephemeral ports in use", p.PeakEphemeralPortsInUse.ToString("N0", Inv));
        Kv(sb, "Peak TIME_WAIT sockets", p.PeakTimeWaitSockets.ToString("N0", Inv));
        Kv(sb, "Peak handle count", p.PeakHandleCount.ToString("N0", Inv));
        Kv(sb, "Peak thread count", p.PeakThreadCount.ToString("N0", Inv));
        Kv(sb, "Max CPU %", p.MaxCpuPercent.ToString("F1", Inv));
        Kv(sb, "Peak working set", $"{(p.PeakWorkingSetBytes / (1024.0 * 1024.0)).ToString("F0", Inv)} MB");
        sb.Append("</tbody></table>");
    }

    private static void Kv(StringBuilder sb, string k, string v) =>
        sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{Svg.Esc(k)}</td><td>{Svg.Esc(v)}</td></tr>");

    private static string OpLabel(string op) => op switch
    {
        OpNames.FindInput => "find input",
        OpNames.Remove => "remove",
        OpNames.Insert => "insert",
        OpNames.FindOutput => "find output",
        _ => op,
    };

    private static string Css() => """
        :root{--fg:#0f172a;--muted:#64748b;--line:#e2e8f0;--bg:#f8fafc;}
        *{box-sizing:border-box}
        body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:var(--fg);margin:0;padding:24px;max-width:1000px;margin:0 auto;background:#fff;line-height:1.45}
        h1{font-size:24px;margin:0 0 4px} h2{font-size:20px;margin:32px 0 8px;border-bottom:2px solid var(--line);padding-bottom:4px}
        h3{font-size:15px;margin:18px 0 6px}
        .muted{color:var(--muted);font-size:13px} code{background:var(--bg);padding:1px 4px;border-radius:3px;font-size:12px}
        .rid{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:0.7em;color:var(--muted);font-weight:600}
        table{border-collapse:collapse;width:100%;margin:6px 0;font-size:13px}
        th,td{border:1px solid var(--line);padding:5px 8px;text-align:left} th{background:var(--bg)}
        table.kv td:first-child{color:var(--muted);width:46%}
        td.hi{font-weight:600} td.diverge{color:#b45309} tr.client-err td{color:#b45309}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:14px;align-items:start}
        .charts{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin:10px 0}
        .chart{width:100%;height:auto;border:1px solid var(--line);border-radius:6px;background:#fff}
        .box{border:1px solid var(--line);border-left-width:4px;border-radius:6px;padding:10px 14px;margin:10px 0;background:var(--bg)}
        .box h3{margin-top:0} .box.info{border-left-color:#2563eb} .box.caveat{border-left-color:#f59e0b}
        .box.ok{border-left-color:#16a34a} .box.warn{border-left-color:#dc2626} .box.verdict{border-left-color:#7c3aed}
        .chart .ct{font-size:13px;font-weight:600;fill:var(--fg)} .chart .axis{stroke:#94a3b8;stroke-width:1}
        .chart .grid{stroke:#eef2f7;stroke-width:1} .chart .tick{font-size:10px;fill:var(--muted)}
        .chart .alabel{font-size:11px;fill:var(--muted)} .chart .leg{font-size:10px;fill:var(--fg)}
        @media(max-width:760px){.grid2,.charts{grid-template-columns:1fr}}
        """;
}
