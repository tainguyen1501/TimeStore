using TimeStore.Contracts.Replication;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Replication;

/// <summary>A bucket's replication rule (admin API).</summary>
public interface IReplicationService
{
    /// <summary>The rule with its backlog, or <c>REPLICATION_NOT_FOUND</c>.</summary>
    ValueTask<Result<ReplicationResponse>> GetAsync(string bucket, CancellationToken ct);

    /// <summary>
    /// Validates, writes and deletes a test object at the destination, then saves the rule (secret encrypted). With
    /// <c>ExistingObjects</c> every current object is queued in the same transaction.
    /// </summary>
    ValueTask<Result<ReplicationResponse>> SetAsync(string bucket, SetReplicationRequest request, CancellationToken ct);

    /// <summary>Removes the rule and what waits in its queue (nothing is deleted at the destination).</summary>
    ValueTask<Result> DeleteAsync(string bucket, CancellationToken ct);

    /// <summary>Writes and deletes a test object at the saved destination.</summary>
    ValueTask<Result<ReplicationResponse>> TestAsync(string bucket, CancellationToken ct);

    /// <summary>Failed changes are sent again, and a paused rule resumes now.</summary>
    ValueTask<Result<ReplicationResponse>> RetryAsync(string bucket, CancellationToken ct);

    /// <summary>
    /// Sends every current object under the rule's prefix again (the destination lost data or was replaced); objects
    /// already there are overwritten with the same content.
    /// </summary>
    ValueTask<Result<ReplicationResponse>> ResyncAsync(string bucket, CancellationToken ct);

    /// <summary>Every rule with its backlog (no failure list), by bucket name: the metrics.</summary>
    ValueTask<IReadOnlyList<ReplicationResponse>> ListAsync(CancellationToken ct);
}

/// <summary>Sends queued changes to the destinations until stopped (the replication worker's loop).</summary>
public interface IReplicationRunner
{
    Task RunAsync(CancellationToken ct);
}

/// <summary>Section <c>TimeStore:Replication</c>, as the services use it.</summary>
/// <param name="Concurrency">Keys sent at the same time, over every rule.</param>
/// <param name="PollInterval">How often the queue is read when no commit woke the worker (a safety net).</param>
/// <param name="BatchDelay">How long the worker waits after a wake-up, so a burst of changes to a key is sent once.</param>
/// <param name="MaxBackoff">Longest pause after repeated temporary failures.</param>
/// <param name="RequestTimeout">Longest time one request to the destination may take (a whole object or part).</param>
public sealed record ReplicationOptions(
    bool Enabled, int Concurrency, TimeSpan PollInterval, TimeSpan BatchDelay, TimeSpan MaxBackoff, TimeSpan RequestTimeout)
{
    public const int DefaultConcurrency = 4;
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultBatchDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(15);

    public static ReplicationOptions Default { get; } = new(
        true, DefaultConcurrency, DefaultPollInterval, DefaultBatchDelay, DefaultMaxBackoff, DefaultRequestTimeout);

    /// <summary>1 s, 2 s, 4 s ... capped at <see cref="MaxBackoff"/>, for the <paramref name="failures"/>-th failure in a row.</summary>
    public TimeSpan Backoff(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 30);
        var delay = TimeSpan.FromSeconds(Math.Pow(2, exponent));
        return delay < MaxBackoff ? delay : MaxBackoff;
    }
}
