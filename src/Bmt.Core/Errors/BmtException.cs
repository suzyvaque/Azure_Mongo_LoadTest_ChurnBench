namespace Bmt.Core.Errors;

/// <summary>
/// A benchmark error carrying its <see cref="BmtErrorType"/> classification. Used by preflight to
/// raise typed, abort-worthy failures (e.g. <see cref="BmtErrorType.DataSetMissing"/>,
/// <see cref="BmtErrorType.IndexMissing"/>) that the runner reports verbatim before any timed run.
/// </summary>
public sealed class BmtException : Exception
{
    public BmtException(BmtErrorType errorType, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorType = errorType;
    }

    public BmtErrorType ErrorType { get; }
}
