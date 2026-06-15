using System.Net.Sockets;
using MongoDB.Driver;

namespace Bmt.Core.Errors;

/// <summary>
/// Classifies any exception thrown during a Task into a <see cref="BmtErrorType"/> (§7.4).
/// The <paramref name="target"/> context routes throttling: Cosmos RU 429s become
/// <see cref="BmtErrorType.CosmosRuThrottling"/>, everything else becomes
/// <see cref="BmtErrorType.ThrottlingOrRateLimit"/>.
/// </summary>
public static class ExceptionClassifier
{
    /// <summary>Cosmos RU "request rate too large" server error code.</summary>
    public const int CosmosRequestRateTooLargeCode = 16500;

    /// <summary>Windows socket error for ephemeral-port exhaustion (WSAEADDRINUSE).</summary>
    private const int WsaeAddrInUse = 10048;

    /// <summary>Windows socket error for buffer/resource exhaustion under heavy churn (WSAENOBUFS).</summary>
    private const int WsaeNoBufs = 10055;

    public static BmtErrorType Classify(Exception exception, TargetKey? target = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Our own pre-classified preflight failures win outright.
        if (exception is BmtException bmt)
        {
            return bmt.ErrorType;
        }

        // Client-side port/buffer exhaustion — must be reported separately from server errors (§7.3).
        if (TryFindSocketException(exception, out var socketError) &&
            socketError is WsaeAddrInUse or WsaeNoBufs ||
            FindInner<SocketException>(exception) is { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.NoBufferSpaceAvailable })
        {
            return BmtErrorType.ClientPortExhaustion;
        }

        if (exception is MongoAuthenticationException)
        {
            return BmtErrorType.AuthenticationFailure;
        }

        // Throttling / rate-limit: Cosmos RU 429 routes to its own bucket.
        if (IsThrottling(exception))
        {
            return target == TargetKey.CosmosRu
                ? BmtErrorType.CosmosRuThrottling
                : BmtErrorType.ThrottlingOrRateLimit;
        }

        // Server-selection timeout: the driver throws System.TimeoutException whose message
        // mentions selecting a server.
        if (FindInner<TimeoutException>(exception) is { } timeout)
        {
            return timeout.Message.Contains("selecting a server", StringComparison.OrdinalIgnoreCase)
                ? BmtErrorType.ServerSelectionTimeout
                : BmtErrorType.Timeout;
        }

        if (exception is MongoExecutionTimeoutException)
        {
            return BmtErrorType.Timeout;
        }

        // Connection-level failures (socket errors are wrapped in MongoConnectionException, which
        // carries an inner SocketException). A timeout-flavored message is a socket timeout.
        if (FindInner<MongoConnectionException>(exception) is { } connEx)
        {
            return connEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? BmtErrorType.SocketTimeout
                : BmtErrorType.ConnectionFailure;
        }

        // DocumentDB Mongo-compatibility gaps surface as command errors (unsupported op/feature).
        if (FindInner<MongoCommandException>(exception) is { } cmd &&
            target == TargetKey.DocumentDb &&
            LooksLikeCompatibilityError(cmd))
        {
            return BmtErrorType.DocumentDbCompatibility;
        }

        if (FindInner<MongoCommandException>(exception) is not null ||
            FindInner<MongoQueryException>(exception) is not null ||
            FindInner<MongoWriteException>(exception) is not null ||
            FindInner<MongoBulkWriteException>(exception) is not null)
        {
            return BmtErrorType.QueryFailure;
        }

        return BmtErrorType.Unknown;
    }

    /// <summary>True if the exception represents a 429 / RU rate-limit (used by retry/backoff).</summary>
    public static bool IsThrottling(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (FindInner<MongoCommandException>(exception) is { } cmd &&
            (cmd.Code == CosmosRequestRateTooLargeCode || MentionsThrottle(cmd.Message)))
        {
            return true;
        }

        if (FindInner<MongoWriteException>(exception) is { } we &&
            (we.WriteError?.Code == CosmosRequestRateTooLargeCode || MentionsThrottle(we.Message)))
        {
            return true;
        }

        // Bulk inserts (seeder) surface RU throttling as write errors inside a bulk exception.
        if (FindInner<MongoBulkWriteException>(exception) is { } bulk &&
            (bulk.WriteErrors.Any(e => e.Code == CosmosRequestRateTooLargeCode) ||
             MentionsThrottle(bulk.Message)))
        {
            return true;
        }

        return MentionsThrottle(exception.Message);
    }

    private static bool MentionsThrottle(string? message) =>
        message is not null &&
        (message.Contains("RetryAfterMs", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("Request rate is large", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("429", StringComparison.Ordinal) ||
         message.Contains("16500", StringComparison.Ordinal));

    private static bool LooksLikeCompatibilityError(MongoCommandException cmd) =>
        cmd.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
        cmd.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
        cmd.Message.Contains("CommandNotSupported", StringComparison.OrdinalIgnoreCase) ||
        cmd.Code == 59 /* CommandNotFound */;

    private static bool TryFindSocketException(Exception exception, out int errorCode)
    {
        var se = FindInner<SocketException>(exception);
        errorCode = se?.ErrorCode ?? 0;
        return se is not null;
    }

    private static T? FindInner<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }

            if (current is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    var found = FindInner<T>(inner);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
