using System.Globalization;
using System.Text;
using Bmt.Core.Metrics;

namespace Bmt.LoadGen.Output;

/// <summary>
/// Writes the §8 CSV companions to the JSON run artifact: a per-second throughput/resource time-series
/// (one row per second — connection open/close rates, per-op QPS, in-flight Tasks, ports/TIME_WAIT/
/// CPU/mem) and a flat latency-summary table (per-op + cycle percentiles). These feed spreadsheets and
/// the HTML report graphs.
/// </summary>
public static class CsvWriter
{
    public static async Task WriteTimeSeriesAsync(RunResult result, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var bySecond = new Dictionary<int, ResourceSample>();
        foreach (var s in result.ResourceSamples)
        {
            bySecond[s.Second] = s;
        }

        var sb = new StringBuilder();
        sb.AppendLine("second,conn_created,conn_closed,find_input_ops,remove_ops,insert_ops,find_output_ops," +
                      "failed_ops,combined_ops,in_flight_tasks,ephemeral_ports,time_wait,handles,threads,cpu_pct,working_set_bytes");

        foreach (var p in result.Throughput)
        {
            bySecond.TryGetValue(p.Second, out var r);
            sb.Append(p.Second).Append(',')
              .Append(p.ConnectionsCreated).Append(',')
              .Append(p.ConnectionsClosed).Append(',')
              .Append(p.FindInputOps).Append(',')
              .Append(p.RemoveOps).Append(',')
              .Append(p.InsertOps).Append(',')
              .Append(p.FindOutputOps).Append(',')
              .Append(p.FailedOps).Append(',')
              .Append(p.CombinedOps).Append(',')
              .Append(p.InFlightTasks).Append(',')
              .Append(r?.EphemeralPortsInUse ?? 0).Append(',')
              .Append(r?.TimeWaitSockets ?? 0).Append(',')
              .Append(r?.HandleCount ?? 0).Append(',')
              .Append(r?.ThreadCount ?? 0).Append(',')
              .Append((r?.CpuPercent ?? 0).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r?.WorkingSetBytes ?? 0)
              .Append('\n');
        }

        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }

    public static async Task WriteLatencySummaryAsync(RunResult result, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine("series,count,min_ms,mean_ms,p50_ms,p90_ms,p95_ms,p99_ms,p999_ms,max_ms");

        foreach (var op in OpNames.Ordered)
        {
            if (result.OperationLatencyMs.TryGetValue(op, out var s))
            {
                AppendRow(sb, op, s);
            }
        }

        AppendRow(sb, "task_cycle", result.TaskCycleLatencyMs);
        AppendRow(sb, "connection_open", result.ConnectionOpenMs);
        AppendRow(sb, "client_create", result.ClientCreateMs);

        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }

    private static void AppendRow(StringBuilder sb, string name, LatencySummary s)
    {
        var c = CultureInfo.InvariantCulture;
        sb.Append(name).Append(',')
          .Append(s.Count).Append(',')
          .Append(s.MinMs.ToString("F3", c)).Append(',')
          .Append(s.MeanMs.ToString("F3", c)).Append(',')
          .Append(s.P50Ms.ToString("F3", c)).Append(',')
          .Append(s.P90Ms.ToString("F3", c)).Append(',')
          .Append(s.P95Ms.ToString("F3", c)).Append(',')
          .Append(s.P99Ms.ToString("F3", c)).Append(',')
          .Append(s.P999Ms.ToString("F3", c)).Append(',')
          .Append(s.MaxMs.ToString("F3", c))
          .Append('\n');
    }
}
