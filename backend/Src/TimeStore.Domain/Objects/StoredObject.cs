using TimeStore.Domain.Blobs;

namespace TimeStore.Domain.Objects;

/// <summary>A name/value pair stored with an object (system headers, user metadata).</summary>
public readonly record struct ObjectHeader(string Name, string Value);

/// <summary>
/// The current version of an object: where its bytes are (<see cref="Data"/>) and what S3 returns about it.
/// Immutable: an overwrite commits a new <see cref="StoredObject"/> pointing at new blobs.
/// </summary>
/// <param name="ETag">Without quotes. Hex MD5 for a single PUT, <c>md5-N</c> for multipart.</param>
/// <param name="ChecksumAlgorithm">Upper-case algorithm name (CRC32, CRC32C, CRC64NVME, SHA1, SHA256) or null.</param>
/// <param name="Checksum">Digest in canonical byte order, or null.</param>
/// <param name="ChecksumType">For multipart objects with a checksum: <c>ChecksumTypes</c> value; null otherwise.</param>
/// <param name="Headers">Stored system headers (Cache-Control, Content-Disposition, ...), lower-case names.</param>
/// <param name="UserMetadata">x-amz-meta-* without the prefix, lower-case names.</param>
public sealed record StoredObject(
    Guid BucketId,
    string Key,
    Guid VersionId,
    ObjectData Data,
    string ETag,
    string ContentType,
    string? ChecksumAlgorithm,
    byte[]? Checksum,
    string? ChecksumType,
    IReadOnlyList<ObjectHeader> Headers,
    IReadOnlyList<ObjectHeader> UserMetadata,
    DateTime CreatedAt)
{
    public const int KeyMaxBytes = 1024;

    /// <summary>Sum of user metadata names and values in UTF-8 bytes (Amazon S3 limit).</summary>
    public const int UserMetadataMaxBytes = 2 * 1024;

    /// <summary>Largest single PUT (Amazon S3 limit; larger objects use multipart).</summary>
    public const long SinglePutMaxBytes = 5L * 1024 * 1024 * 1024;

    public const string DefaultContentType = "binary/octet-stream";

    /// <summary>
    /// The client sees version id <c>null</c>: written while the bucket was unversioned or suspended. Stores set it on
    /// read and decide it on write (from the bucket's state); new objects start as null versions.
    /// </summary>
    public bool IsNullVersion { get; init; } = true;

    public long Size => Data.Size;

    public ObjectSummary ToSummary() => new(Key, Size, ETag, CreatedAt);
}

/// <summary>One page of a listing in key order.</summary>
/// <param name="CommonPrefixes">Prefixes rolled up at the delimiter, in key order.</param>
/// <param name="LastEntry">The last key or common prefix returned: where the next page starts (exclusive).</param>
public sealed record ObjectPage(
    IReadOnlyList<ObjectSummary> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? LastEntry);

/// <summary>When a commit may happen.</summary>
public enum CommitCondition
{
    /// <summary>Insert or replace (last commit wins).</summary>
    Always,

    /// <summary>Only when no object exists under the key (<c>If-None-Match: *</c>).</summary>
    IfAbsent,
}

public enum CommitStatus
{
    Committed,

    /// <summary>The bucket was deleted before the commit; nothing was written.</summary>
    BucketMissing,

    /// <summary>The condition did not hold; nothing was written.</summary>
    ConditionFailed,
}

/// <summary>Outcome of committing an object.</summary>
/// <param name="Replaced">The data of the version that was overwritten, to delete after the commit.</param>
/// <param name="Replaced">Data no version references any more (an unversioned overwrite, a replaced null version).</param>
/// <param name="Version">The version written (committed only).</param>
public readonly record struct ObjectCommit(CommitStatus Status, ObjectData? Replaced, ObjectVersion? Version = null);

/// <summary>A verified checksum: upper-case algorithm name and digest in canonical byte order.</summary>
public sealed record ObjectChecksum(string Algorithm, byte[] Digest);

/// <summary>S3 key rules beyond the length limit enforced by the router.</summary>
public static class ObjectKeys
{
    /// <summary>
    /// False for keys with characters XML 1.0 cannot carry (U+0000–U+001F except tab, LF, CR; U+FFFE, U+FFFF):
    /// they could never be listed. Rejected on write (docs/research/s3-objects.md, decision 1).
    /// </summary>
    public static bool IsListable(ReadOnlySpan<char> key)
    {
        foreach (var c in key)
        {
            if ((c < ' ' && c is not '\t' and not '\n' and not '\r') || c is '\uFFFE' or '\uFFFF')
                return false;
        }

        return true;
    }
}

public enum BucketDeletion
{
    Deleted,
    NotFound,
    NotEmpty,
}

/// <param name="FreedBlobs">
/// Part blobs of the uploads still in progress, aborted with the bucket (Amazon S3: only objects keep a bucket from
/// being deleted), for the caller to delete after the commit.
/// </param>
public sealed record BucketDeletionResult(BucketDeletion Status, IReadOnlyList<BlobLocator> FreedBlobs);
