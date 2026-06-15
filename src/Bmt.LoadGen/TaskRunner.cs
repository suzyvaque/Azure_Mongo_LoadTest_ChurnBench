using System.Diagnostics;
using Bmt.Core;
using Bmt.Core.Connections;
using Bmt.Core.Errors;
using Bmt.Core.Metrics;
using Bmt.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.LoadGen;

/// <summary>
/// Executes exactly one Task = the strict 4-op cycle under test (test_instruction.md §2.1), all keyed
/// on the <c>ReqId</c> field (never the <c>_id</c> point-read):
/// <list type="number">
///   <item><c>find</c> input (calc_input by ReqId)</item>
///   <item>sleep <c>taskSleepMs</c> (calc-time substitute; between input-find and output-remove)</item>
///   <item><c>remove</c> output (calc_output by ReqId) — mandatory, NEVER upsert</item>
///   <item><c>insert</c> output (calc_output)</item>
///   <item><c>find</c> output (calc_output by ReqId)</item>
/// </list>
/// A brand-new <see cref="TaskConnection"/> (fresh MongoClient, pool size 1, no reuse) is created and
/// disposed per Task. taskSleepMs is excluded from per-op latencies but included in the full cycle.
/// Throttling/errors are CLASSIFIED and recorded (not silently retried) so latency is not hidden —
/// Cosmos RU 429s land in the <c>CosmosRuThrottling</c> bucket (§7.4).
/// </summary>
public sealed class TaskRunner
{
    private readonly TaskConnectionFactory _factory;
    private readonly MetricsCollector _metrics;
    private readonly int _taskSleepMs;
    private readonly TargetKey _target;
    private readonly string _outputPayload;

    public TaskRunner(
        TaskConnectionFactory factory,
        MetricsCollector metrics,
        TargetKey target,
        int taskSleepMs)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _target = target;
        _taskSleepMs = Math.Max(0, taskSleepMs);

        // A modest fixed output payload (~1 KB base64). Output sizing is not part of the dataset spec;
        // the input dataset carries the size distribution. Built once and reused as an immutable string.
        _outputPayload = Convert.ToBase64String(new byte[768]);
    }

    /// <summary>Run a single Task against the given ReqId. Never throws — failures are classified/recorded.</summary>
    public async Task RunAsync(string reqId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reqId);

        var cycle = Stopwatch.StartNew();
        _metrics.OnTaskStart();
        var success = false;
        TaskConnection? conn = null;

        try
        {
            var createSw = Stopwatch.StartNew();
            conn = _factory.Create();
            createSw.Stop();
            _metrics.RecordClientCreate(createSw.Elapsed.TotalMilliseconds);

            var inputFilter = Builders<CalcInputDoc>.Filter.Eq(d => d.ReqId, reqId);
            var outputFilter = Builders<CalcOutputDoc>.Filter.Eq(d => d.ReqId, reqId);

            // Op 1: find input by ReqId (read).
            await TimedAsync(OpNames.FindInput, ct, async () =>
                await conn.CalcInput.Find(inputFilter).FirstOrDefaultAsync(ct).ConfigureAwait(false))
                .ConfigureAwait(false);

            // Step 2: calc-time substitute sleep (excluded from per-op latency, included in cycle).
            if (_taskSleepMs > 0)
            {
                await Task.Delay(_taskSleepMs, ct).ConfigureAwait(false);
            }

            // Op 2: remove output by ReqId (write) — mandatory, never upsert.
            await TimedAsync(OpNames.Remove, ct, async () =>
                await conn.CalcOutput.DeleteManyAsync(outputFilter, ct).ConfigureAwait(false))
                .ConfigureAwait(false);

            // Op 3: insert output (write) — plain insert, never upsert.
            var doc = BuildOutputDoc(reqId);
            await TimedAsync(OpNames.Insert, ct, async () =>
            {
                await conn.CalcOutput.InsertOneAsync(doc, cancellationToken: ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            // Op 4: find output by ReqId (read-back).
            await TimedAsync(OpNames.FindOutput, ct, async () =>
                await conn.CalcOutput.Find(outputFilter).FirstOrDefaultAsync(ct).ConfigureAwait(false))
                .ConfigureAwait(false);

            success = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Run is ending — not a workload failure; leave success=false without recording an error.
        }
        catch (Exception ex)
        {
            _metrics.RecordError(ExceptionClassifier.Classify(ex, _target));
        }
        finally
        {
            conn?.Dispose();
            cycle.Stop();
            _metrics.OnTaskEnd(success, cycle.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Time one op; record success/failure and (on success) its latency. Per-op errors abort the Task.</summary>
    private async Task TimedAsync(string opName, CancellationToken ct, Func<Task<object?>> op)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await op().ConfigureAwait(false);
            sw.Stop();
            _metrics.RecordOp(opName, sw.Elapsed.TotalMilliseconds, success: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _metrics.RecordOp(opName, sw.Elapsed.TotalMilliseconds, success: false);
            throw;
        }
    }

    private CalcOutputDoc BuildOutputDoc(string reqId)
    {
        var now = DateTime.UtcNow;
        return new CalcOutputDoc
        {
            Id = reqId,
            ReqId = reqId,
            StartTime = now.ToString("O"),
            EndTime = now.AddMilliseconds(_taskSleepMs).ToString("O"),
            Output = _outputPayload,
            OutputFormatCd = new BsonDocument { { "fmt", "b64" } },
        };
    }
}
