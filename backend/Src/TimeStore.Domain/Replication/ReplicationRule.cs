namespace TimeStore.Domain.Replication;

/// <summary>
/// Where a bucket's changes go (docs/research/replication.md): another S3 endpoint and bucket, reached path-style and
/// signed with SigV4. One rule per bucket (R1). The secret is stored only as <see cref="SecretCiphertext"/>.
/// </summary>
/// <param name="Prefix">Only keys starting with it are replicated ("" = every key).</param>
/// <param name="ReplicateDeletes">Send deletes too; off by default, so a mistake on the source is not copied.</param>
/// <param name="PausedUntil">After a temporary destination failure nothing is sent before this time (backoff).</param>
/// <param name="Failures">Temporary destination failures in a row (the backoff grows with it); 0 after a success.</param>
/// <param name="LastError">The last destination failure (rule-wide), never a secret.</param>
public sealed record ReplicationRule(
    Guid Id,
    Guid BucketId,
    string Endpoint,
    string TargetBucket,
    string Region,
    string AccessKeyId,
    byte[] SecretCiphertext,
    string Prefix,
    bool ReplicateDeletes,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public const int EndpointMaxLength = 2048;
    public const int RegionMaxLength = 64;
    public const int AccessKeyIdMaxLength = 256;
    public const int SecretKeyMaxLength = 512;
    public const string DefaultRegion = "us-east-1";

    public DateTime PausedUntil { get; init; }

    public int Failures { get; init; }

    public string? LastError { get; init; }

    public DateTime? LastSuccessAt { get; init; }

    /// <summary>Whether <paramref name="key"/> is in the rule's scope (ordinal prefix, as S3 prefixes).</summary>
    public bool Covers(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>What a queued change did to its key. The worker sends the key's current state either way.</summary>
public enum ReplicationOperation
{
    Put,
    Delete,
}

public enum ReplicationChangeStatus
{
    Pending,
    Failed,
}

/// <summary>One row of the outbox: a key of a rule's bucket changed (its content is read when it is sent).</summary>
public sealed record ReplicationChange(
    long Id,
    Guid RuleId,
    string Key,
    ReplicationOperation Operation,
    DateTime CreatedAt,
    int Attempts,
    DateTime NextAttemptAt,
    ReplicationChangeStatus Status,
    string? LastError);

/// <summary>What waits for a rule: pending changes, the oldest one, and the changes that failed (newest first).</summary>
public sealed record ReplicationBacklog(
    long Pending, DateTime? OldestPendingAt, long Failed, IReadOnlyList<ReplicationChange> Failures);
