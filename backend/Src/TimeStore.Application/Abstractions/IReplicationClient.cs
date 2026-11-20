using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;

namespace TimeStore.Application.Abstractions;

/// <summary>
/// Another S3 endpoint that receives replicated objects (docs/research/replication.md). Implemented by the host with
/// its own small S3 client (SigV4, path-style); never logs the secret, a signature or a body.
/// </summary>
public interface IReplicationClient
{
    /// <summary>
    /// Sends <paramref name="source"/> under its key: one PUT for a single blob, a multipart upload with the source's
    /// own parts for a multipart object. Every request carries the blob's <c>Content-MD5</c>, so the destination
    /// refuses a body that changed on the way; the stored content type, headers and user metadata go with it.
    /// </summary>
    /// <param name="openBlob">Opens a blob of the source (the client never knows where blobs live).</param>
    ValueTask<ReplicationOutcome> PutAsync(
        ReplicationDestination destination, StoredObject source, Func<BlobLocator, CancellationToken, ValueTask<Stream>> openBlob,
        CancellationToken ct);

    /// <summary>Deletes the key at the destination (a key that is not there is not an error).</summary>
    ValueTask<ReplicationOutcome> DeleteAsync(ReplicationDestination destination, string key, CancellationToken ct);

    /// <summary>Writes and deletes a small test object (<see cref="TestKeyPrefix"/>) at the destination.</summary>
    ValueTask<ReplicationOutcome> TestAsync(ReplicationDestination destination, CancellationToken ct);

    public const string TestKeyPrefix = ".timestore-replication-test/";
}

/// <summary>A destination with its secret in plain text (in memory only, decrypted for one use).</summary>
public sealed record ReplicationDestination(Uri Endpoint, string Bucket, string Region, string AccessKeyId, string SecretKey)
{
    /// <summary>Never prints the secret (records print every member by default).</summary>
    public override string ToString() => $"{Endpoint} bucket {Bucket} ({Region}, key {AccessKeyId})";
}

/// <summary>How a send ended; decides what happens to the queued changes.</summary>
public enum ReplicationFailure
{
    /// <summary>Done: the destination holds the key's current state.</summary>
    None,

    /// <summary>
    /// The destination is unreachable, slow or busy (network error, timeout, 5xx, throttling): the rule pauses with a
    /// growing backoff; nothing becomes failed, however long it lasts.
    /// </summary>
    Temporary,

    /// <summary>The destination refuses every request (credentials, missing bucket): every pending change fails.</summary>
    DestinationRejected,

    /// <summary>The destination refuses this object (4xx for it): the key's changes fail until a retry.</summary>
    ObjectRejected,

    /// <summary>The source content could not be read (released and deleted meanwhile): the key is tried again later.</summary>
    SourceUnavailable,
}

/// <param name="Error">What went wrong: the destination's S3 error code and status, or the exception type. Never a secret.</param>
public readonly record struct ReplicationOutcome(ReplicationFailure Failure, string? Error)
{
    public static ReplicationOutcome Success => new(ReplicationFailure.None, null);

    public bool IsSuccess => Failure == ReplicationFailure.None;
}

/// <summary>A blob of the source could not be opened; the client answers <see cref="ReplicationFailure.SourceUnavailable"/>.</summary>
public sealed class ReplicationSourceException(string message, Exception inner) : Exception(message, inner);

/// <summary>What the replication runner reports (the host logs it).</summary>
public interface IReplicationObserver
{
    /// <summary>A send did not succeed and what was done about it.</summary>
    void Failed(Guid ruleId, string key, ReplicationOutcome outcome);

    /// <summary>A rule's destination worked again after temporary failures.</summary>
    void Resumed(Guid ruleId, int failures);
}
