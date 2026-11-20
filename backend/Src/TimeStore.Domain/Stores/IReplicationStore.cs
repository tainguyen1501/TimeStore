using TimeStore.Domain.Replication;

namespace TimeStore.Domain.Stores;

/// <summary>
/// Replication rules and the outbox (<c>replication_queue</c>). Implemented by the metadata provider: the outbox rows
/// are written by the object write methods of <see cref="IMetadataStore"/> in the commit's own transaction, so a
/// committed change is never lost. Pinned by <c>ReplicationStoreContract</c>.
/// </summary>
public interface IReplicationStore
{
    /// <summary>
    /// Creates the bucket's rule, or replaces its settings (same id, pending changes kept). With
    /// <paramref name="queueExisting"/> every current object of the bucket under the prefix is queued in the same
    /// transaction, so no change made before or after is missed. False when the bucket does not exist.
    /// </summary>
    ValueTask<bool> SaveRuleAsync(ReplicationRule rule, bool queueExisting, CancellationToken ct);

    /// <summary>
    /// Queues every current object of the bucket under the rule's prefix again (resync: the destination lost data or
    /// was replaced); returns how many. Nothing when the bucket has no rule.
    /// </summary>
    ValueTask<int> ResyncAsync(Guid bucketId, DateTime now, CancellationToken ct);

    ValueTask<ReplicationRule?> FindRuleAsync(Guid bucketId, CancellationToken ct);

    ValueTask<IReadOnlyList<ReplicationRule>> ListRulesAsync(CancellationToken ct);

    /// <summary>Deletes the rule and its queue; false when the bucket has none.</summary>
    ValueTask<bool> DeleteRuleAsync(Guid bucketId, CancellationToken ct);

    /// <summary>
    /// Pending changes due at <paramref name="now"/>, oldest first, of rules not paused at <paramref name="now"/>; at
    /// most <paramref name="limit"/>.
    /// </summary>
    ValueTask<IReadOnlyList<ReplicationChange>> ListDueChangesAsync(DateTime now, int limit, CancellationToken ct);

    /// <summary>The earliest time a pending change becomes due (its own retry time or its rule's pause), or null.</summary>
    ValueTask<DateTime?> NextDueAsync(CancellationToken ct);

    /// <summary>The key's current state reached the destination: deletes its changes up to <paramref name="upToId"/>.</summary>
    ValueTask CompleteAsync(Guid ruleId, string key, long upToId, DateTime at, CancellationToken ct);

    /// <summary>A problem of this key only (its content could not be read): retried at <paramref name="nextAttempt"/>.</summary>
    ValueTask DeferAsync(Guid ruleId, string key, long upToId, DateTime nextAttempt, string error, CancellationToken ct);

    /// <summary>The destination refused this key for good: its changes up to <paramref name="upToId"/> become failed.</summary>
    ValueTask FailAsync(Guid ruleId, string key, long upToId, string error, CancellationToken ct);

    /// <summary>
    /// The destination is unreachable or busy: the rule pauses until <paramref name="until"/> and counts one more
    /// failure. Nothing becomes failed.
    /// </summary>
    ValueTask PauseRuleAsync(Guid ruleId, DateTime until, string error, CancellationToken ct);

    /// <summary>The destination refused the rule itself (credentials, missing bucket): every pending change fails.</summary>
    ValueTask FailRuleAsync(Guid ruleId, string error, CancellationToken ct);

    /// <summary>Failed changes become pending again and the rule's pause ends; returns how many changes.</summary>
    ValueTask<int> RetryAsync(Guid ruleId, DateTime now, CancellationToken ct);

    /// <summary>Pending and failed counts, the oldest pending change, and up to <paramref name="maxFailures"/> failures.</summary>
    ValueTask<ReplicationBacklog> GetBacklogAsync(Guid ruleId, int maxFailures, CancellationToken ct);

    /// <summary>
    /// Completes when a commit has queued a change since the last call (or at once if one did before); the worker's
    /// wake-up. Spurious completions are allowed.
    /// </summary>
    ValueTask WaitForChangesAsync(CancellationToken ct);
}
