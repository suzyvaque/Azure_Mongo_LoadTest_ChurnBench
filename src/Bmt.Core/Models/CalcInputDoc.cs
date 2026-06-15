using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Bmt.Core.Models;

/// <summary>
/// Flat <c>calc_input</c> document (test_instruction.md §3.1, corrected schema commit 5baf211).
/// The <c>{ results:[{_id,value}], ok:1 }</c> form in the original doc was a Mongo *response
/// envelope*, not the stored layout — we persist top-level scalar fields so a <c>{ ReqId: 1 }</c>
/// index is possible. All four Task ops key on <see cref="ReqId"/>, never on <see cref="Id"/>.
/// </summary>
public sealed class CalcInputDoc
{
    /// <summary>Primary key: a sequential row counter as a string (e.g. "1653"). Never queried.</summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    [BsonElement("_id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Logical request id (== <see cref="Id"/>). Indexed (unique); all ops key on this.</summary>
    [BsonElement("ReqId")]
    public string ReqId { get; set; } = string.Empty;

    [BsonElement("CalculatorFileNm")]
    public string CalculatorFileNm { get; set; } = string.Empty;

    [BsonElement("CalculatorVersion")]
    public string CalculatorVersion { get; set; } = string.Empty;

    [BsonElement("SkipCalculation")]
    public bool SkipCalculation { get; set; }

    /// <summary>Base64 payload sized so the WHOLE BSON doc hits its 6/16/50/58 KB bucket.</summary>
    [BsonElement("Input")]
    public string Input { get; set; } = string.Empty;

    /// <summary>Real array, e.g. <c>[0]</c>.</summary>
    [BsonElement("SuccessExitCodeList")]
    public int[] SuccessExitCodeList { get; set; } = Array.Empty<int>();

    [BsonElement("ReqClass")]
    public string ReqClass { get; set; } = string.Empty;
}
