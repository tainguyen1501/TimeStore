using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;

namespace TimeStore.Domain.Uploads;

/// <summary>
/// An upload in progress (CreateMultipartUpload). Carries the object's future headers: they are given at creation,
/// not at completion.
/// </summary>
/// <param name="ChecksumAlgorithm">Upper-case algorithm every part must use, or null.</param>
/// <param name="ChecksumType"><see cref="ChecksumTypes"/> value when an algorithm is set.</param>
public sealed record MultipartUpload(
    Guid UploadId,
    Guid BucketId,
    string Key,
    string ContentType,
    string? ChecksumAlgorithm,
    string? ChecksumType,
    IReadOnlyList<ObjectHeader> Headers,
    IReadOnlyList<ObjectHeader> UserMetadata,
    DateTime CreatedAt)
{
    public const int MinPartNumber = 1;
    public const int MaxPartNumber = 10_000;

    /// <summary>Every part except the last must be at least this large (checked at completion).</summary>
    public const long MinPartSize = 5L * 1024 * 1024;

    public const long MaxPartSize = 5L * 1024 * 1024 * 1024;
}

public static class ChecksumTypes
{
    /// <summary>Checksum of the part checksums, suffixed <c>-N</c>.</summary>
    public const string Composite = "COMPOSITE";

    /// <summary>Checksum of the whole object (CRC types only), combined from the part CRCs.</summary>
    public const string FullObject = "FULL_OBJECT";
}

/// <summary>A stored part of an upload in progress.</summary>
public sealed record UploadPart(Guid UploadId, int PartNumber, BlobLocator Blob, byte[]? Checksum, DateTime CreatedAt)
{
    /// <summary>A part's ETag is the hex MD5 of its blob, without quotes.</summary>
    public string ETag => Blob.Md5Hex;
}

/// <summary>An upload as listings show it.</summary>
public sealed record UploadSummary(string Key, Guid UploadId, DateTime CreatedAt);

/// <summary>One page of ListMultipartUploads: uploads ordered by key, then upload id (= initiation time).</summary>
/// <param name="NextKeyMarker">Where the next page starts: the last key or common prefix returned.</param>
/// <param name="NextUploadIdMarker">The last upload id returned (null when the page ended on a common prefix).</param>
public sealed record UploadPage(
    IReadOnlyList<UploadSummary> Uploads,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextKeyMarker,
    Guid? NextUploadIdMarker);

/// <summary>One page of ListParts.</summary>
public sealed record PartPage(IReadOnlyList<UploadPart> Parts, bool IsTruncated);

public enum PartCommitStatus
{
    Committed,

    /// <summary>The upload was completed or aborted first; the caller deletes the new blob.</summary>
    UploadMissing,
}

/// <param name="Replaced">The previous blob of the same part number, to delete after the commit.</param>
public readonly record struct PartCommit(PartCommitStatus Status, BlobLocator? Replaced);

public enum CompletionStatus
{
    Completed,
    UploadMissing,
    BucketMissing,

    /// <summary>The commit condition (<c>If-None-Match</c>) did not hold.</summary>
    ConditionFailed,

    /// <summary>A listed part was re-uploaded after the caller read it; nothing was written (re-read and retry).</summary>
    PartsChanged,
}

/// <param name="Replaced">The object version that was overwritten, to delete after the commit.</param>
/// <param name="Discarded">Blobs of uploaded parts the completion did not list, to delete after the commit.</param>
public sealed record UploadCompletion(
    CompletionStatus Status, ObjectData? Replaced, IReadOnlyList<BlobLocator> Discarded, ObjectVersion? Version = null);

/// <summary>An upload found by age (abandoned-upload cleanup): enough to abort it.</summary>
public sealed record UploadRef(Guid BucketId, string Key, Guid UploadId);
