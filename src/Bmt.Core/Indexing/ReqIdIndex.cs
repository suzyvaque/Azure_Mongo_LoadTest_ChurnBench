using Bmt.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.Core.Indexing;

/// <summary>
/// The mandatory <c>ReqId</c> index spec (test_instruction.md §2.1/§5, handoff §5).
/// Must exist on BOTH collections on ALL three backends before any timed run — without it every
/// <c>find/remove({ReqId})</c> is a full collection scan over 100k docs.
///
/// Uniqueness is target-aware. The handoff (§5) makes the INDEX mandatory but a unique index merely
/// "fine and documents intent" (optional). On Cosmos RU a unique index can only be defined on a
/// brand-new, never-materialized collection, and doing so on this account is unreliable (the async
/// drop races the create and silently wipes it); a NON-unique index, by contrast, creates cleanly
/// and persists on a populated collection. So we use unique on mongo-vm/documentdb and non-unique on
/// cosmos-ru. Uniqueness is still guaranteed by the seeder (sequential ids) and the ReqId-keyed
/// remove+insert workload.
/// </summary>
public static class ReqIdIndex
{
    public const string FieldName = "ReqId";
    public const string IndexName = "ReqId_1";

    /// <summary>True if the input <c>ReqId</c> index should be unique for the given backend.</summary>
    public static bool UniqueForTarget(TargetKey target) => target != TargetKey.CosmosRu;

    /// <summary><c>{ ReqId: 1 }</c> index model for <c>calc_input</c>; unique per <paramref name="unique"/>.</summary>
    public static CreateIndexModel<CalcInputDoc> InputModel(bool unique) =>
        new(
            Builders<CalcInputDoc>.IndexKeys.Ascending(x => x.ReqId),
            new CreateIndexOptions { Unique = unique, Name = IndexName });

    /// <summary>Non-unique <c>{ ReqId: 1 }</c> index model for <c>calc_output</c>.</summary>
    public static CreateIndexModel<CalcOutputDoc> OutputModel() =>
        new(
            Builders<CalcOutputDoc>.IndexKeys.Ascending(x => x.ReqId),
            new CreateIndexOptions { Unique = false, Name = IndexName });

    /// <summary>
    /// True if the given index catalog (from <c>listIndexes</c>) contains a single-field ascending
    /// index on <c>ReqId</c>. Used by preflight check 2 (fail with IndexMissing otherwise).
    /// </summary>
    public static bool ExistsIn(IEnumerable<BsonDocument> indexes) => FindReqIdIndex(indexes) is not null;

    /// <summary>
    /// True if the <c>ReqId</c> index in the given catalog is declared <c>unique</c>. Used by preflight
    /// check 2 to record the unique-vs-non-unique divergence (unique on mongo-vm/documentdb,
    /// non-unique on cosmos-ru). Returns false if the index is absent or non-unique.
    /// </summary>
    public static bool IsUniqueIn(IEnumerable<BsonDocument> indexes)
    {
        var ix = FindReqIdIndex(indexes);
        return ix is not null &&
               ix.TryGetValue("unique", out var unique) &&
               unique.IsBoolean &&
               unique.AsBoolean;
    }

    /// <summary>Returns the single-field ascending <c>{ ReqId: 1 }</c> index document, or null.</summary>
    private static BsonDocument? FindReqIdIndex(IEnumerable<BsonDocument> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        foreach (var ix in indexes)
        {
            if (!ix.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument key)
            {
                continue;
            }

            if (key.ElementCount == 1 &&
                key.TryGetValue(FieldName, out var dir) &&
                dir.IsNumeric &&
                dir.ToDouble() == 1.0)
            {
                return ix;
            }
        }

        return null;
    }
}
