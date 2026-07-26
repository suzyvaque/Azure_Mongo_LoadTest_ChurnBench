using MongoDB.Driver;

namespace Bmt.Core.Connections;

/// <summary>
/// Best-effort, lock-free counter of retryable command failures — the events that TRIGGER a driver
/// retry when <c>RetryWrites=true</c> is in force (documentdb/mongo). The official driver performs
/// retryable writes/reads transparently (no per-attempt event is emitted for the retry itself), so
/// rather than reconstruct hidden attempts we count the retryable FAILURES that provoke them. Combined
/// with the per-run <c>RetryWritesEnabled</c> flag and the §7.4 <c>ErrorsByType</c> taxonomy (e.g.
/// <c>ThrottlingOrRateLimit</c>) and the server-side 429 status class, this lets a run correlate "how
/// often did an operation hit a retryable condition" against latency/throughput after the fact.
///
/// Every method is O(1) via <see cref="System.Threading.Interlocked"/> so enabling it adds negligible
/// load to the generator (important: the client must not perturb the very measurement it records).
/// Handshake commands (hello/isMaster/saslStart/saslContinue) are excluded — those are connection
/// establishment, counted separately as <c>ConnectionsFailed</c>, not workload retries.
/// </summary>
public sealed class RetryEventCounters
{
    private long _commandFailures;
    private long _retryableCommandFailures;

    /// <summary>Total non-handshake command failures observed (workload ops: find/remove/insert).</summary>
    public long CommandFailures => System.Threading.Interlocked.Read(ref _commandFailures);

    /// <summary>Subset of <see cref="CommandFailures"/> whose error is retryable (a retry trigger).</summary>
    public long RetryableCommandFailures => System.Threading.Interlocked.Read(ref _retryableCommandFailures);

    /// <summary>Record a non-handshake command failure; classify whether it is a retryable condition.</summary>
    public void OnCommandFailed(Exception? failure)
    {
        System.Threading.Interlocked.Increment(ref _commandFailures);
        if (IsRetryable(failure))
        {
            System.Threading.Interlocked.Increment(ref _retryableCommandFailures);
        }
    }

    /// <summary>
    /// Retryable = the driver would transparently retry it under retryable writes/reads: an explicit
    /// <c>RetryableWriteError</c> label, or a transient network / not-primary / node-recovering /
    /// timeout condition. Conservative by design (best-effort), matching the plan's approved approach.
    /// </summary>
    private static bool IsRetryable(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return false;
            case MongoException me when me.HasErrorLabel("RetryableWriteError"):
                return true;
            case MongoConnectionException:
            case MongoNotPrimaryException:
            case MongoNodeIsRecoveringException:
            case TimeoutException:
                return true;
            default:
                return ex.InnerException is not null && IsRetryable(ex.InnerException);
        }
    }
}
