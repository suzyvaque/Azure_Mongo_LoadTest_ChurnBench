namespace Bmt.Core;

/// <summary>
/// Fixed names that every backend shares (database + the two collections).
/// These come from the handoff doc and are identical across mongo-vm, cosmos-ru and documentdb.
/// </summary>
public static class BmtConstants
{
    public const string DatabaseName = "bmt_db";
    public const string CalcInputCollection = "calc_input";
    public const string CalcOutputCollection = "calc_output";

    /// <summary>Exact document count the dataset must contain (test_instruction.md §3).</summary>
    public const int RequiredInputDocCount = 100_000;

    /// <summary>Fixed RNG seed so calc_input is byte-identical across all three targets.</summary>
    public const int DatasetSeed = 42;
}
