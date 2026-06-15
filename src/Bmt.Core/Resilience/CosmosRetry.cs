using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.Core.Resilience;

/// <summary>
/// 429 / RetryAfterMs backoff for Cosmos RU (handoff §3). Cosmos RU does not support retryable
/// writes, so the workload (and the seeder's bulk inserts / batched deletes) must catch
/// "request rate too large" and back off using the server-provided <c>RetryAfterMs</c> when present.
/// Used ONLY for the cosmos-ru target; the other backends are not throttled.
/// </summary>
public static class CosmosRetry
{
    private const int DefaultMaxAttempts = 50;

    /// <summary>
    /// Lower bound on each backoff delay. Cosmos often returns <c>RetryAfterMs=1</c> under sustained
    /// pressure; honoring that literally would burn the whole attempt budget in milliseconds, so we
    /// floor every wait to give the shared RU/s budget time to recover.
    /// </summary>
    private const int MinDelayMs = 25;

    /// <summary>Run an async operation with RU-throttle backoff and return its result.</summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && Errors.ExceptionClassifier.IsThrottling(ex))
            {
                // Wait for the LONGER of the server-provided hint and our exponential backoff, then
                // floor it — sustained throttling needs a growing, non-trivial pause to clear.
                var serverHint = GetRetryAfter(ex) ?? TimeSpan.Zero;
                var backoff = Backoff(attempt);
                var delay = serverHint > backoff ? serverHint : backoff;
                if (delay < TimeSpan.FromMilliseconds(MinDelayMs))
                {
                    delay = TimeSpan.FromMilliseconds(MinDelayMs);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Run an async operation (no result) with RU-throttle backoff.</summary>
    public static Task ExecuteAsync(
        Func<Task> operation,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<bool>(
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            maxAttempts,
            cancellationToken);
    }

    /// <summary>
    /// Extract the server-provided retry delay from a Cosmos RU throttle response, if present.
    /// Cosmos returns <c>RetryAfterMs</c> in the error document; we also parse it from the message
    /// as a fallback.
    /// </summary>
    public static TimeSpan? GetRetryAfter(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        BsonDocument? result = exception switch
        {
            MongoCommandException cmd => cmd.Result,
            _ => null,
        };

        if (result is not null && TryReadRetryAfter(result, out var fromDoc))
        {
            return fromDoc;
        }

        return TryParseRetryAfterFromMessage(exception.Message, out var fromMsg) ? fromMsg : null;
    }

    private static bool TryReadRetryAfter(BsonDocument doc, out TimeSpan delay)
    {
        foreach (var name in new[] { "RetryAfterMs", "retryAfterMs", "RetryAfterMilliseconds" })
        {
            if (doc.TryGetValue(name, out var v) && v.IsNumeric)
            {
                delay = TimeSpan.FromMilliseconds(v.ToDouble());
                return true;
            }
        }

        delay = default;
        return false;
    }

    private static bool TryParseRetryAfterFromMessage(string message, out TimeSpan delay)
    {
        delay = default;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        var idx = message.IndexOf("RetryAfterMs", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var digits = new string(message[idx..]
            .SkipWhile(c => !char.IsDigit(c))
            .TakeWhile(char.IsDigit)
            .ToArray());

        if (int.TryParse(digits, out var ms) && ms > 0)
        {
            delay = TimeSpan.FromMilliseconds(ms);
            return true;
        }

        return false;
    }

    private static TimeSpan Backoff(int attempt)
    {
        // Exponential with a cap; jitter avoids synchronized retries across many concurrent Tasks.
        var baseMs = Math.Min(2000, 50 * (1 << Math.Min(attempt, 5)));
        var jitter = Random.Shared.Next(0, 50);
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }
}
