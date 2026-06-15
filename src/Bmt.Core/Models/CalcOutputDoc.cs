using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Bmt.Core.Models;

/// <summary>
/// Flat <c>calc_output</c> document (test_instruction.md §3.2, corrected schema commit 5baf211).
/// Written by each Task via <c>remove</c> + <c>insert</c> (never upsert), then read back by
/// <see cref="ReqId"/>. The <c>ReqId</c> field is indexed (non-unique).
/// </summary>
public sealed class CalcOutputDoc
{
    /// <summary>Primary key (== <see cref="ReqId"/> of the Task). Not used to drive ops.</summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    [BsonElement("_id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Logical request id. Indexed (non-unique); all ops key on this field.</summary>
    [BsonElement("ReqId")]
    public string ReqId { get; set; } = string.Empty;

    /// <summary>Task start timestamp (ISO-8601 string).</summary>
    [BsonElement("StartTime")]
    public string StartTime { get; set; } = string.Empty;

    /// <summary>Task end timestamp (ISO-8601 string).</summary>
    [BsonElement("EndTime")]
    public string EndTime { get; set; } = string.Empty;

    /// <summary>Result payload (base64).</summary>
    [BsonElement("Output")]
    public string Output { get; set; } = string.Empty;

    /// <summary>Real object of any shape, e.g. <c>{ "fmt": "b64" }</c>.</summary>
    [BsonElement("OutputFormatCd")]
    public BsonDocument OutputFormatCd { get; set; } = new();
}
