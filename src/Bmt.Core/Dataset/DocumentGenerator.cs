using Bmt.Core.Configuration;
using Bmt.Core.Models;
using MongoDB.Bson;

namespace Bmt.Core.Dataset;

/// <summary>The product of generating one <c>calc_input</c> document, with its measured BSON size.</summary>
public readonly record struct GeneratedDoc(CalcInputDoc Doc, string BucketName, int TargetBytes, int ActualBytes)
{
    /// <summary>True if the whole-document BSON size hit the bucket target exactly.</summary>
    public bool IsExact => ActualBytes == TargetBytes;
}

/// <summary>
/// Deterministic <c>calc_input</c> document generator (test_instruction.md §3, handoff §5).
///
/// WHOLE-DOCUMENT BSON sizing: every field except <see cref="CalcInputDoc.Input"/> is fixed/random
/// metadata; the <c>Input</c> base64 string is padded so the ENTIRE serialized BSON document equals
/// the bucket's target byte size. We measure the doc with an empty <c>Input</c>, then add exactly the
/// remaining bytes as ASCII base64 characters (1 char == 1 BSON byte), and verify the final size.
///
/// Determinism: content for document <c>id</c> is driven by an RNG seeded from
/// <c>mix(seed, id)</c>, so any id reproduces byte-identically regardless of run order — which is
/// what makes seeding resumable and identical across all three backends.
/// </summary>
public sealed class DocumentGenerator
{
    private const string Base64Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    private static readonly string[] CalculatorFiles =
        { "PricingEngine.dll", "RiskCalc.dll", "GreeksEngine.dll", "VarModel.dll", "CurveBuilder.dll" };

    private static readonly string[] CalculatorVersions =
        { "1.0.0", "2.3.1", "3.1.4", "4.0.2", "5.2.0" };

    private static readonly string[] ReqClasses = { "A", "B", "C", "D" };

    private readonly BucketPlan _plan;
    private readonly int _seed;

    public DocumentGenerator(DatasetConfig dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        _plan = BucketPlan.Build(dataset);
        _seed = dataset.Seed;
    }

    public int DocumentCount => _plan.DocumentCount;

    /// <summary>Generate the document for a 1-based id, sized to its bucket's whole-doc byte target.</summary>
    public GeneratedDoc Generate(int id)
    {
        var bucket = _plan.BucketForId(id);
        var rng = new Random(Mix(_seed, id));
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var doc = new CalcInputDoc
        {
            Id = key,
            ReqId = key, // ReqId == _id (logical request id); ALL ops key on this field.
            CalculatorFileNm = CalculatorFiles[rng.Next(CalculatorFiles.Length)],
            CalculatorVersion = CalculatorVersions[rng.Next(CalculatorVersions.Length)],
            SkipCalculation = false,
            SuccessExitCodeList = new[] { 0 },
            ReqClass = ReqClasses[rng.Next(ReqClasses.Length)],
            Input = string.Empty,
        };

        // Bytes contributed by everything except the Input payload content.
        var baseBytes = doc.ToBson().Length;
        var needed = bucket.SizeBytes - baseBytes;
        if (needed < 0)
        {
            throw new InvalidOperationException(
                $"Bucket '{bucket.Name}' target {bucket.SizeBytes} B is smaller than the fixed " +
                $"document overhead {baseBytes} B (id {id}); raise the bucket size.");
        }

        doc.Input = RandomBase64(needed, rng);

        // Verify and correct (base64 is ASCII so the 1:1 assumption holds; this guards regressions).
        var actual = doc.ToBson().Length;
        if (actual != bucket.SizeBytes)
        {
            var delta = bucket.SizeBytes - actual;
            var newLen = doc.Input.Length + delta;
            if (newLen >= 0)
            {
                doc.Input = RandomBase64(newLen, rng);
                actual = doc.ToBson().Length;
            }
        }

        return new GeneratedDoc(doc, bucket.Name, bucket.SizeBytes, actual);
    }

    private static string RandomBase64(int length, Random rng)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Base64Alphabet[rng.Next(Base64Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>Well-mixed deterministic per-document seed from (datasetSeed, id).</summary>
    private static int Mix(int seed, int id)
    {
        unchecked
        {
            var h = (uint)seed;
            h = (h ^ (uint)id) * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (int)h;
        }
    }
}
