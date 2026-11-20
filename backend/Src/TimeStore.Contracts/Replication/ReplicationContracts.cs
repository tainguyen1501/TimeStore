namespace TimeStore.Contracts.Replication;

/// <summary>Creates or replaces a bucket's replication rule (docs/api/admin.md "Replication").</summary>
/// <param name="Endpoint">The destination's S3 endpoint, <c>https://host[:port]</c>; reached path-style.</param>
/// <param name="Bucket">The destination bucket (it must exist).</param>
/// <param name="Region">The destination's signing region; <c>us-east-1</c> when null (Cloudflare R2: <c>auto</c>).</param>
/// <param name="SecretKey">
/// Required for a new rule, or when the endpoint or the access key changes; null keeps the stored one. Never returned.
/// </param>
/// <param name="Prefix">Only keys starting with it are replicated; "" or null for every key.</param>
/// <param name="ReplicateDeletes">Also delete on the destination (off: deletes on the source are not copied).</param>
/// <param name="ExistingObjects">Also copy the objects already in the bucket (backfill), not only later changes.</param>
public sealed record SetReplicationRequest(
    string Endpoint,
    string Bucket,
    string? Region,
    string AccessKeyId,
    string? SecretKey = null,
    string? Prefix = null,
    bool ReplicateDeletes = false,
    bool ExistingObjects = true);

/// <summary>A bucket's replication rule and its backlog. The secret key is never part of it.</summary>
/// <param name="PausedUntil">The destination failed for a temporary reason: nothing is sent before this time.</param>
/// <param name="LastError">The last rule-wide destination failure, or null.</param>
/// <param name="LastSuccessAt">When a change last reached the destination.</param>
public sealed record ReplicationResponse(
    Guid Id,
    string SourceBucket,
    string Endpoint,
    string Bucket,
    string Region,
    string AccessKeyId,
    string Prefix,
    bool ReplicateDeletes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? PausedUntil,
    string? LastError,
    DateTime? LastSuccessAt,
    ReplicationBacklogResponse Backlog);

/// <param name="Pending">Changes waiting to be sent.</param>
/// <param name="OldestPendingAt">When the oldest waiting change was made (the lag), or null.</param>
/// <param name="Failed">Changes the destination refused; sent again by a retry.</param>
/// <param name="Failures">The newest failed changes (at most 100).</param>
public sealed record ReplicationBacklogResponse(
    long Pending, DateTime? OldestPendingAt, long Failed, IReadOnlyList<ReplicationFailureResponse> Failures);

/// <param name="Operation"><c>put</c> or <c>delete</c>.</param>
public sealed record ReplicationFailureResponse(string Key, string Operation, int Attempts, string? Error, DateTime ChangedAt);
