namespace Bmt.Core.Errors;

/// <summary>
/// Exception classification buckets (test_instruction.md §7.4). Every failure is classified into
/// exactly one of these — never collapsed into a single "failure". Cosmos RU 429s route to
/// <see cref="CosmosRuThrottling"/> (not the generic <see cref="ThrottlingOrRateLimit"/>), and
/// client-side port starvation routes to <see cref="ClientPortExhaustion"/> so client limits are
/// not misattributed to the database.
/// </summary>
public enum BmtErrorType
{
    Timeout,
    ConnectionFailure,
    ServerSelectionTimeout,
    SocketTimeout,
    AuthenticationFailure,
    ThrottlingOrRateLimit,
    CosmosRuThrottling,
    DocumentDbCompatibility,
    QueryFailure,
    ClientPortExhaustion,
    DataSetMissing,
    IndexMissing,
    Unknown,
}
