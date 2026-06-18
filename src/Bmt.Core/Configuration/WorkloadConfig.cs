using System.Text.Json.Serialization;

namespace Bmt.Core.Configuration;

/// <summary>Chooses between the original full 4-op Task cycle and single-operation isolation mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkloadMode
{
    /// <summary>Original behavior: find(input) → sleep → remove(output) → insert(output) → find(output).</summary>
    FullWorkload,

    /// <summary>One operation per connection — isolates a single op for targeted latency measurement.</summary>
    SingleOp,
}

/// <summary>Which single operation to run when <see cref="WorkloadMode.SingleOp"/> is active.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SingleOpType
{
    /// <summary>find on calc_input by ReqId (cold-socket read, the most expensive per-connection op).</summary>
    FindInput,

    /// <summary>
    /// insert into calc_output (no prior remove; accumulates docs — acceptable for short isolated tests).
    /// </summary>
    InsertOutput,
}

/// <summary>
/// Controls which operations each Task executes. Defaults to <see cref="WorkloadMode.FullWorkload"/> so
/// the original benchmark behavior is unchanged unless the config explicitly selects otherwise.
/// <para>
/// The no-reuse model is preserved in all modes: one fresh MongoClient lifecycle per task/request,
/// <c>maxPoolSize=1</c>, <c>minPoolSize=0</c>. In single-op mode the calc-time substitute sleep
/// (<c>TaskSleepMs</c>) is skipped — the timed window is purely the op itself.
/// </para>
/// </summary>
public sealed class WorkloadConfig
{
    /// <summary>
    /// <c>full-workload</c> (default) = original 4-op cycle.
    /// <c>single-op</c> = one op selected by <see cref="SingleOpType"/>.
    /// </summary>
    public WorkloadMode Mode { get; set; } = WorkloadMode.FullWorkload;

    /// <summary>
    /// Which single operation to run. Only used when <see cref="Mode"/> = <c>SingleOp</c>.
    /// <c>find-input</c> (default): find on calc_input — measures cold-connection read cost.
    /// <c>insert-output</c>: insert into calc_output — measures cold-connection write cost.
    /// </summary>
    public SingleOpType SingleOpType { get; set; } = SingleOpType.FindInput;

    public void Validate()
    {
        // Enum values are validated by JSON deserialization; no cross-field invariants here.
    }

    /// <summary>Short token used in folder/file names: "full-workload", "find-input", or "insert-output".</summary>
    public string Token() => Mode switch
    {
        WorkloadMode.FullWorkload => "full-workload",
        WorkloadMode.SingleOp => SingleOpType switch
        {
            SingleOpType.FindInput => "find-input",
            SingleOpType.InsertOutput => "insert-output",
            _ => "single-op",
        },
        _ => "full-workload",
    };
}
