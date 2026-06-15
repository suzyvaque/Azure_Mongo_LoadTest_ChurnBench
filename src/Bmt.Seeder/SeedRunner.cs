using System.Diagnostics;
using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Connections;
using Bmt.Core.Dataset;
using Bmt.Core.Errors;
using Bmt.Core.Indexing;
using Bmt.Core.Models;
using Bmt.Core.Resilience;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.Seeder;

/// <summary>
/// Implements <c>prepare-data</c> (test_instruction.md §5): seed exactly the configured document
/// count into <c>calc_input</c> with whole-document BSON sizing per bucket, and create the mandatory
/// <c>ReqId</c> indexes on BOTH collections. Idempotent and resumable; Cosmos RU writes use
/// <c>RetryWrites=false</c> + 429/RetryAfterMs backoff and NEVER change the provisioned RU/s.
/// </summary>
internal sealed class SeedRunner
{
    private const int DuplicateKeyCode = 11000;

    private readonly BmtConfig _config;
    private readonly TargetKey _target;
    private readonly bool _force;

    public SeedRunner(BmtConfig config, TargetKey target, bool force)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _target = target;
        _force = force;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var dataset = _config.Dataset;
        ConsoleLog.Info($"prepare-data target={TargetConnection.CliName(_target)} " +
                        $"docCount={dataset.DocumentCount:N0} seed={dataset.Seed} force={_force}");
        ConsoleLog.Info($"Bucket plan: {string.Join(", ", dataset.Buckets.Select(b => $"{b.Name} {b.SizeBytes / 1024}KB×{b.Count:N0}"))} " +
                        $"(mean≈{dataset.MeanBytes() / 1024.0:N1} KB)");

        var connectionString = TargetConnection.ResolveConnectionString(_target);
        ConsoleLog.Info($"Connection: {ConnectionStringMasker.Mask(connectionString)}");

        var client = AdminClientFactory.Create(_target, connectionString);
        var db = client.GetDatabase(BmtConstants.DatabaseName);
        var input = db.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);

        // 1. --force empties BOTH collections via small batched deletes. We delete (not drop) so the
        //    pre-existing collections and their "private path" stay intact per handoff §4; small
        //    batches are 429-aware on Cosmos (handoff §3).
        if (_force)
        {
            ConsoleLog.Info("--force: emptying calc_input and calc_output via small batched deletes.");
            await DeleteAllAsync(db.GetCollection<BsonDocument>(BmtConstants.CalcInputCollection), BmtConstants.CalcInputCollection, ct)
                .ConfigureAwait(false);
            await DeleteAllAsync(db.GetCollection<BsonDocument>(BmtConstants.CalcOutputCollection), BmtConstants.CalcOutputCollection, ct)
                .ConfigureAwait(false);
        }

        // 2. Inspect starting state (handoff §4 says collections were emptied — verify & log).
        var initialCount = await input.CountDocumentsAsync(FilterDefinition<CalcInputDoc>.Empty, cancellationToken: ct)
            .ConfigureAwait(false);
        ConsoleLog.Info(initialCount == 0
            ? "calc_input initial count = 0 (empty, as expected per handoff §4)."
            : $"calc_input initial count = {initialCount:N0}.");

        // 3. Seed the contiguous remainder. Ids are sequential "1".."N", so a partial collection
        //    resumes from count+1 (unordered InsertMany tolerates duplicate-key on overlap).
        var startId = (int)initialCount + 1;
        if (initialCount >= dataset.DocumentCount)
        {
            ConsoleLog.Info($"calc_input already has {initialCount:N0} docs (>= {dataset.DocumentCount:N0}); skipping insert.");
        }
        else
        {
            if (startId > 1)
            {
                ConsoleLog.Info($"Resuming seed from id {startId:N0} ({dataset.DocumentCount - startId + 1:N0} remaining).");
            }

            await SeedAsync(input, startId, ct).ConfigureAwait(false);
        }

        // 4. Create the mandatory ReqId index on BOTH collections AFTER seeding (handoff §5).
        //    Uniqueness is target-aware: unique on mongo-vm/documentdb, non-unique on cosmos-ru
        //    (Cosmos cannot reliably define a unique index on this account — see ReqIdIndex).
        await EnsureIndexesAsync(db, ct).ConfigureAwait(false);

        // 5. Verify final state.
        await VerifyAsync(db, input, ct).ConfigureAwait(false);
    }

    private async Task SeedAsync(IMongoCollection<CalcInputDoc> input, int startId, CancellationToken ct)
    {
        var dataset = _config.Dataset;
        var generator = new DocumentGenerator(dataset);
        var batchSize = _config.Seeder.InsertBatchFor(_target);
        var stats = new BucketSizeStats();
        var sw = Stopwatch.StartNew();

        var batch = new List<CalcInputDoc>(batchSize);
        var inserted = 0L;
        for (var id = startId; id <= dataset.DocumentCount; id++)
        {
            ct.ThrowIfCancellationRequested();
            var gen = generator.Generate(id);
            stats.Record(gen.BucketName, gen.TargetBytes, gen.ActualBytes);
            batch.Add(gen.Doc);

            if (batch.Count >= batchSize)
            {
                await InsertBatchAsync(input, batch, ct).ConfigureAwait(false);
                inserted += batch.Count;
                batch.Clear();
                if (inserted % (batchSize * 10L) == 0)
                {
                    var rate = inserted / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    ConsoleLog.Info($"  inserted {inserted:N0}/{dataset.DocumentCount - startId + 1:N0} ({rate:N0} docs/s)");
                }
            }
        }

        if (batch.Count > 0)
        {
            await InsertBatchAsync(input, batch, ct).ConfigureAwait(false);
            inserted += batch.Count;
        }

        sw.Stop();
        ConsoleLog.Info($"Insert complete: {inserted:N0} docs in {sw.Elapsed.TotalSeconds:N1}s " +
                        $"({inserted / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0} docs/s).");
        stats.Log();
    }

    private async Task InsertBatchAsync(IMongoCollection<CalcInputDoc> input, List<CalcInputDoc> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var docs = batch.ToArray();
        var options = new InsertManyOptions { IsOrdered = false };

        async Task InsertOnce()
        {
            try
            {
                await input.InsertManyAsync(docs, options, ct).ConfigureAwait(false);
            }
            catch (MongoBulkWriteException ex)
            {
                // Let Cosmos 429 propagate so the backoff wrapper retries.
                if (ExceptionClassifier.IsThrottling(ex))
                {
                    throw;
                }

                // Tolerate duplicate-key only (resume / retry idempotency); rethrow anything else.
                var nonDuplicate = ex.WriteErrors.Where(e => e.Code != DuplicateKeyCode).ToList();
                if (nonDuplicate.Count > 0)
                {
                    throw;
                }
            }
        }

        if (_target == TargetKey.CosmosRu)
        {
            await CosmosRetry.ExecuteAsync(InsertOnce, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            await InsertOnce().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Create the mandatory ReqId index on both collections, AFTER seeding (handoff §5). Check-first
    /// and skip when already present (idempotent / resumable). Uniqueness is target-aware: unique on
    /// mongo-vm/documentdb, non-unique on cosmos-ru. A non-unique index creates cleanly and persists
    /// on a populated Cosmos collection; a unique one cannot be defined reliably there (see ReqIdIndex).
    /// </summary>
    private async Task EnsureIndexesAsync(IMongoDatabase db, CancellationToken ct)
    {
        var input = db.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);
        var output = db.GetCollection<CalcOutputDoc>(BmtConstants.CalcOutputCollection);
        var unique = ReqIdIndex.UniqueForTarget(_target);

        if (await HasReqIdIndexAsync(db, BmtConstants.CalcInputCollection, ct).ConfigureAwait(false))
        {
            ConsoleLog.Info($"ReqId index already present on {BmtConstants.CalcInputCollection}.");
        }
        else
        {
            await CosmosAwareAsync(() => input.Indexes.CreateOneAsync(ReqIdIndex.InputModel(unique), cancellationToken: ct), ct)
                .ConfigureAwait(false);
            ConsoleLog.Info($"Created {(unique ? "unique" : "non-unique")} ReqId index on {BmtConstants.CalcInputCollection}.");
        }

        if (await HasReqIdIndexAsync(db, BmtConstants.CalcOutputCollection, ct).ConfigureAwait(false))
        {
            ConsoleLog.Info($"ReqId index already present on {BmtConstants.CalcOutputCollection}.");
        }
        else
        {
            await CosmosAwareAsync(() => output.Indexes.CreateOneAsync(ReqIdIndex.OutputModel(), cancellationToken: ct), ct)
                .ConfigureAwait(false);
            ConsoleLog.Info($"Created non-unique ReqId index on {BmtConstants.CalcOutputCollection}.");
        }

        ConsoleLog.Info($"ReqId indexes ensured: {(unique ? "unique" : "non-unique")} on {BmtConstants.CalcInputCollection}, " +
                        $"non-unique on {BmtConstants.CalcOutputCollection}.");
    }

    private async Task DeleteAllAsync(IMongoCollection<BsonDocument> raw, string name, CancellationToken ct)
    {
        ConsoleLog.Info($"--force: emptying {name} (batched delete)...");
        var pageSize = _config.Seeder.DeleteBatchSize;
        var projection = Builders<BsonDocument>.Projection.Include("_id");
        var deleted = 0L;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await raw
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Project(projection)
                .Limit(pageSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            var ids = page.Select(d => d["_id"]).ToArray();
            var filter = Builders<BsonDocument>.Filter.In("_id", ids);
            var result = await CosmosAwareAsync(() => raw.DeleteManyAsync(filter, ct), ct).ConfigureAwait(false);
            deleted += result.DeletedCount;
        }

        ConsoleLog.Info($"--force: deleted {deleted:N0} docs from {name}.");
    }

    private async Task VerifyAsync(IMongoDatabase db, IMongoCollection<CalcInputDoc> input, CancellationToken ct)
    {
        var dataset = _config.Dataset;
        var count = await input.CountDocumentsAsync(FilterDefinition<CalcInputDoc>.Empty, cancellationToken: ct)
            .ConfigureAwait(false);
        if (count != dataset.DocumentCount)
        {
            throw new BmtException(
                BmtErrorType.DataSetMissing,
                $"Post-seed verification failed: calc_input has {count:N0} docs, expected {dataset.DocumentCount:N0}.");
        }

        var inputHasIndex = await HasReqIdIndexAsync(db, BmtConstants.CalcInputCollection, ct).ConfigureAwait(false);
        var outputHasIndex = await HasReqIdIndexAsync(db, BmtConstants.CalcOutputCollection, ct).ConfigureAwait(false);
        if (!inputHasIndex || !outputHasIndex)
        {
            throw new BmtException(
                BmtErrorType.IndexMissing,
                $"Post-seed verification failed: ReqId index present on calc_input={inputHasIndex}, calc_output={outputHasIndex} (both required).");
        }

        ConsoleLog.Info($"VERIFIED: calc_input count = {count:N0}; ReqId index present on both collections. prepare-data OK.");
    }

    private static async Task<bool> HasReqIdIndexAsync(IMongoDatabase db, string collection, CancellationToken ct)
    {
        var coll = db.GetCollection<BsonDocument>(collection);
        using var cursor = await coll.Indexes.ListAsync(ct).ConfigureAwait(false);
        var indexes = await cursor.ToListAsync(ct).ConfigureAwait(false);
        return ReqIdIndex.ExistsIn(indexes);
    }

    private async Task<T> CosmosAwareAsync<T>(Func<Task<T>> op, CancellationToken ct) =>
        _target == TargetKey.CosmosRu
            ? await CosmosRetry.ExecuteAsync(op, cancellationToken: ct).ConfigureAwait(false)
            : await op().ConfigureAwait(false);

    private Task CosmosAwareAsync(Func<Task> op, CancellationToken ct) =>
        _target == TargetKey.CosmosRu
            ? CosmosRetry.ExecuteAsync(op, cancellationToken: ct)
            : op();
}
