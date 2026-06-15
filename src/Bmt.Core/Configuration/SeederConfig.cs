namespace Bmt.Core.Configuration;

/// <summary>
/// Seeder tuning. Cosmos RU uses smaller insert batches to limit 429 pressure under the shared
/// 40,000 RU/s budget (handoff §5); the RU/s itself is NEVER changed by the tool.
/// </summary>
public sealed class SeederConfig
{
    /// <summary>InsertMany batch size for mongo-vm / documentdb (no throttling expected).</summary>
    public int InsertBatchSize { get; set; } = 1_000;

    /// <summary>Smaller InsertMany batch size for cosmos-ru to ease RU throttling.</summary>
    public int CosmosInsertBatchSize { get; set; } = 100;

    /// <summary>Page size for the batched <c>--force</c> delete (also 429-aware on cosmos).</summary>
    public int DeleteBatchSize { get; set; } = 500;

    public void Validate()
    {
        if (InsertBatchSize <= 0 || CosmosInsertBatchSize <= 0 || DeleteBatchSize <= 0)
        {
            throw new InvalidOperationException("Seeder batch sizes must all be > 0.");
        }
    }

    /// <summary>Effective insert batch size for a target (cosmos uses the smaller value).</summary>
    public int InsertBatchFor(TargetKey target) =>
        target == TargetKey.CosmosRu ? CosmosInsertBatchSize : InsertBatchSize;
}
