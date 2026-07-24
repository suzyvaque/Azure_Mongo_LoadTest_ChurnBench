using System.Diagnostics;
using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Connections;
using Bmt.Core.Errors;
using Bmt.Core.Metrics;
using Bmt.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.LoadGen;

/// <summary>
/// Executes one Task under the configured workload mode (test_instruction.md §2.1).
/// <para>
/// <b>Full-workload mode</b> (default): the strict 4-op cycle, all keyed on the <c>ReqId</c> field:
/// <list type="number">
///   <item><c>find</c> input (calc_input by ReqId)</item>
///   <item>sleep <c>taskSleepMs</c> (calc-time substitute; between input-find and output-remove)</item>
///   <item><c>remove</c> output (calc_output by ReqId) — mandatory, NEVER upsert</item>
///   <item><c>insert</c> output (calc_output)</item>
///   <item><c>find</c> output (calc_output by ReqId)</item>
/// </list>
/// </para>
/// <para>
/// <b>Single-op mode</b>: one operation per connection, selected by <see cref="WorkloadConfig.SingleOpType"/>:
/// <list type="bullet">
///   <item><c>find-input</c>: find on calc_input — isolates cold-connection read cost.</item>
///   <item><c>insert-output</c>: insert into calc_output — isolates cold-connection write cost (accumulates docs; suitable for short isolated tests).</item>
/// </list>
/// The calc-time substitute sleep is skipped in single-op mode.
/// </para>
/// A brand-new <see cref="TaskConnection"/> (fresh MongoClient, pool size 1, no reuse) is created and
/// disposed per Task in all modes. Throttling/errors are CLASSIFIED and recorded (not silently retried).
/// </summary>
public sealed class TaskRunner
{
    private readonly TaskConnectionFactory _factory;
    private readonly MetricsCollector _metrics;
    private readonly int _taskSleepMs;
    private readonly TargetKey _target;
    private readonly WorkloadConfig _workload;
    private readonly string _outputPayload;

    /// <summary>
    /// Absolute UTC instant at which the <see cref="WorkloadMode.Hold"/> keep-Ready loop must release its
    /// connection so the drain phase can complete. Set by the orchestrator once the run window is known.
    /// Ignored for the churn/single-op workloads. Defaults to <see cref="DateTime.MaxValue"/> (no bound).
    /// </summary>
    public DateTime HoldDeadlineUtc { get; set; } = DateTime.MaxValue;

    public TaskRunner(
        TaskConnectionFactory factory,
        MetricsCollector metrics,
        TargetKey target,
        int taskSleepMs,
        WorkloadConfig workload)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _workload = workload ?? throw new ArgumentNullException(nameof(workload));
        _target = target;
        _taskSleepMs = Math.Max(0, taskSleepMs);

        // A modest fixed output payload (~1 KB base64). Output sizing is not part of the dataset spec;
        // the input dataset carries the size distribution. Built once and reused as an immutable string.
        _outputPayload = Convert.ToBase64String(new byte[768]);
    }

    /// <summary>Notify metrics that a Task has been OFFERED to the runtime (called at inject time).</summary>
    public void OnScheduled() => _metrics.OnTaskScheduled();

    /// <summary>
    /// Run a single Task against the given ReqId. Never throws — failures are classified/recorded.
    /// <paramref name="scheduledUtc"/> is the instant the Task was offered to the runtime (stamped by the
    /// launcher just before dispatch), so scheduler-queue latency (dispatch delay) and the true
    /// offered-to-finished latency can be measured separately from execution time (§2).
    /// </summary>
    public async Task RunAsync(string reqId, DateTime scheduledUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reqId);

        var taskStartedUtc = DateTime.UtcNow;
        var cycle = Stopwatch.StartNew();
        _metrics.OnTaskStart();
        var success = false;
        TaskConnection? conn = null;

        try
        {
            // Hold mode: replacement Tasks injected by the closed-loop gate right as the window closes must
            // NOT open a fresh connection (a pointless cold-connect during teardown that would skew the tail
            // and delay drain). Treat as an immediate no-op so the gate slot is released cleanly.
            if (_workload.Mode == WorkloadMode.Hold && DateTime.UtcNow >= HoldDeadlineUtc)
            {
                success = true;
                return;
            }

            var createSw = Stopwatch.StartNew();
            conn = _factory.Create();
            createSw.Stop();
            _metrics.RecordClientCreate(createSw.Elapsed.TotalMilliseconds);

            // Connection demand begins now: the first operation triggers server selection + a fresh
            // physical connection open (§3). Stamp it so demand-to-ready (user-observed cold-connection
            // latency) can be measured against the driver's connection-ready event, and count that this
            // Task reached demand (the reconciliation floor: created ≈ Tasks that reached demand).
            conn.Lifecycle?.MarkDemand();
            _metrics.OnConnectionDemand();

            if (_workload.Mode == WorkloadMode.Hold)
            {
                await RunHoldAsync(conn, reqId, ct).ConfigureAwait(false);
            }
            else if (_workload.Mode == WorkloadMode.SingleOp)
            {
                await RunSingleOpAsync(conn, reqId, ct).ConfigureAwait(false);
            }
            else
            {
                await RunFullWorkloadAsync(conn, reqId, ct).ConfigureAwait(false);
            }

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
            var lifecycle = conn?.Lifecycle;
            conn?.Dispose();
            cycle.Stop();
            var taskFinishedUtc = DateTime.UtcNow;
            var schedQueueMs = Math.Max(0, (taskStartedUtc - scheduledUtc).TotalMilliseconds);
            var execMs = Math.Max(0, (taskFinishedUtc - taskStartedUtc).TotalMilliseconds);
            var offeredToFinishedMs = Math.Max(0, (taskFinishedUtc - scheduledUtc).TotalMilliseconds);
            _metrics.OnTaskEnd(success, cycle.Elapsed.TotalMilliseconds, schedQueueMs, execMs, offeredToFinishedMs);

            // §3: per-Task cold-connection latency (demand → driver-ready), when the connection opened.
            if (lifecycle?.DemandToReadyMs is { } demandToReady)
            {
                _metrics.RecordConnectionDemandToReady(demandToReady);
            }
        }
    }

    /// <summary>Full 4-op cycle with calc-time sleep (original benchmark behavior).</summary>
    private async Task RunFullWorkloadAsync(TaskConnection conn, string reqId, CancellationToken ct)
    {
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
    }

    /// <summary>
    /// Saturation-hold cycle: open ONE connection (initial find drives it to Ready) and keep it Ready until
    /// <see cref="HoldDeadlineUtc"/>, issuing a light keepalive find every <c>TaskSleepMs</c> so neither the
    /// driver nor the server idle-closes the socket. The connection stays counted in ActiveReady the whole
    /// time, so a closed-loop population of these Tasks parks a fixed concurrent-Ready count. Any op failure
    /// propagates so the Task ends and the gate frees a slot for a replacement (self-healing toward the cap).
    /// </summary>
    private async Task RunHoldAsync(TaskConnection conn, string reqId, CancellationToken ct)
    {
        var inputFilter = Builders<CalcInputDoc>.Filter.Eq(d => d.ReqId, reqId);

        // Initial find: forces server selection + physical connection open → Ready.
        await TimedAsync(OpNames.FindInput, ct, async () =>
            await conn.CalcInput.Find(inputFilter).FirstOrDefaultAsync(ct).ConfigureAwait(false))
            .ConfigureAwait(false);

        // Keepalive interval; guard against a 0/negative sleep degenerating into a tight find loop.
        var keepaliveMs = _taskSleepMs > 0 ? _taskSleepMs : 10_000;

        while (!ct.IsCancellationRequested)
        {
            var remaining = HoldDeadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // Sleep no longer than the time left in the window so drain starts promptly at the deadline.
            var wait = remaining < TimeSpan.FromMilliseconds(keepaliveMs)
                ? remaining
                : TimeSpan.FromMilliseconds(keepaliveMs);
            await Task.Delay(wait, ct).ConfigureAwait(false);

            if (DateTime.UtcNow >= HoldDeadlineUtc)
            {
                break;
            }

            // Keepalive find on the SAME held connection — keeps the socket demonstrably alive and Ready.
            await TimedAsync(OpNames.FindInput, ct, async () =>
                await conn.CalcInput.Find(inputFilter).FirstOrDefaultAsync(ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Single-op mode: one operation per connection, no sleep.</summary>
    private async Task RunSingleOpAsync(TaskConnection conn, string reqId, CancellationToken ct)
    {
        switch (_workload.SingleOpType)
        {
            case SingleOpType.FindInput:
            {
                var filter = Builders<CalcInputDoc>.Filter.Eq(d => d.ReqId, reqId);
                await TimedAsync(OpNames.FindInput, ct, async () =>
                    await conn.CalcInput.Find(filter).FirstOrDefaultAsync(ct).ConfigureAwait(false))
                    .ConfigureAwait(false);
                break;
            }

            case SingleOpType.InsertOutput:
            {
                // Use a fresh ObjectId as _id (not reqId) since there is no prior remove in single-op
                // mode — reusing reqId as _id would collide with existing calc_output docs.
                var doc = BuildOutputDoc(reqId, useReqIdAsId: false);
                await TimedAsync(OpNames.Insert, ct, async () =>
                {
                    await conn.CalcOutput.InsertOneAsync(doc, cancellationToken: ct).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled SingleOpType: {_workload.SingleOpType}");
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

    private CalcOutputDoc BuildOutputDoc(string reqId, bool useReqIdAsId = true)
    {
        var now = DateTime.UtcNow;
        return new CalcOutputDoc
        {
            Id = useReqIdAsId ? reqId : MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
            ReqId = reqId,
            StartTime = now.ToString("O"),
            EndTime = now.AddMilliseconds(_taskSleepMs).ToString("O"),
            Output = _outputPayload,
            OutputFormatCd = new BsonDocument { { "fmt", "b64" } },
        };
    }
}
