namespace TimeStore.Protocol.Xml;

/// <summary>The owner shown in listings. Single tenant: the root key's identity.</summary>
public sealed record S3Owner(string Id, string DisplayName);

public sealed record S3BucketEntry(string Name, DateTime CreatedAt);

/// <param name="ETag">Without quotes; the writer adds them.</param>
public sealed record S3ObjectEntry(string Key, DateTime LastModified, string ETag, long Size, string StorageClass);

/// <summary>One page of an object listing (shared by ListObjects v1 and v2).</summary>
/// <param name="NextToken">Next continuation token (v2) or next marker (v1) when truncated.</param>
public sealed record S3ObjectListing(
    IReadOnlyList<S3ObjectEntry> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextToken);

/// <summary>A multi-object delete request.</summary>
/// <param name="Quiet">Report only errors, not every deleted key.</param>
public sealed record S3DeleteRequest(bool Quiet, IReadOnlyList<S3DeleteEntry> Objects);

public sealed record S3DeleteEntry(string Key, string? VersionId);

/// <summary>A key that could not be deleted, with its S3 error.</summary>
public sealed record S3DeleteError(string Key, string Code, string Message);

/// <summary>
/// A <c>Deleted</c> entry: <paramref name="VersionId"/> when the request named a version;
/// <paramref name="DeleteMarker"/> when a delete marker was created or removed, with its id.
/// </summary>
public sealed record S3Deleted(string Key, string? VersionId, bool DeleteMarker, string? DeleteMarkerVersionId);

/// <summary>A <c>PutBucketVersioning</c> body: <c>Enabled</c> or <c>Suspended</c>, and the MFA delete setting if sent.</summary>
public sealed record S3VersioningRequest(string Status, string? MfaDelete);

/// <summary>One <c>Version</c> or <c>DeleteMarker</c> entry of ListObjectVersions (ETag without quotes).</summary>
public sealed record S3VersionEntry(
    string Key, string VersionId, bool IsLatest, bool IsDeleteMarker, DateTime LastModified, string ETag, long Size);

/// <summary>A ListObjectVersions page; both next markers are set when truncated (the version one may be null).</summary>
public sealed record S3VersionListing(
    IReadOnlyList<S3VersionEntry> Versions,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextVersionIdMarker);

/// <summary>
/// One lifecycle <c>Rule</c> as sent. <paramref name="ExpirationDate"/> is the raw text (the caller validates it);
/// <paramref name="HasTagFilter"/> and <paramref name="HasTransition"/> mark parts TimeStore does not implement.
/// </summary>
public sealed record S3LifecycleRule(
    string? Id,
    bool Enabled,
    string Prefix,
    long? SizeGreaterThan,
    long? SizeLessThan,
    bool HasTagFilter,
    int? ExpirationDays,
    string? ExpirationDate,
    bool ExpiredObjectDeleteMarker,
    int? NoncurrentDays,
    int? NewerNoncurrentVersions,
    int? AbortIncompleteUploadDays,
    bool HasTransition,
    bool PrefixInRule = false);

/// <summary>Values of the versioning <c>Status</c> (and <c>MfaDelete</c>) elements.</summary>
public static class S3VersioningStatus
{
    public const string Enabled = "Enabled";
    public const string Suspended = "Suspended";
}

/// <summary>Values of the S3 <c>StorageClass</c> element.</summary>
public static class S3StorageClasses
{
    public const string Standard = "STANDARD";
}

/// <summary>One part in <c>ListPartsResult</c>.</summary>
/// <param name="Checksum">Base64 checksum of the part, or null.</param>
public sealed record S3PartEntry(int PartNumber, DateTime LastModified, string ETag, long Size, string? Checksum);

/// <summary>A ListParts page.</summary>
/// <param name="ChecksumAlgorithm">Upper-case name, or null; also names the part checksum element.</param>
public sealed record S3PartListing(
    string Bucket,
    string Key,
    string UploadId,
    int PartNumberMarker,
    int? NextPartNumberMarker,
    int MaxParts,
    bool IsTruncated,
    IReadOnlyList<S3PartEntry> Parts,
    string? ChecksumAlgorithm,
    string? ChecksumType);

/// <summary>One upload in <c>ListMultipartUploadsResult</c>.</summary>
public sealed record S3UploadEntry(string Key, string UploadId, DateTime Initiated);

/// <summary>A ListMultipartUploads page.</summary>
public sealed record S3UploadListing(
    IReadOnlyList<S3UploadEntry> Uploads,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextUploadIdMarker);

/// <summary>A parsed CompleteMultipartUpload document, parts in document order.</summary>
public sealed record S3CompleteRequest(IReadOnlyList<S3CompletePart> Parts);

/// <param name="ETag">Without surrounding quotes.</param>
/// <param name="ChecksumAlgorithm">Upper-case name from the element (<c>ChecksumCRC32</c> → <c>CRC32</c>), or null.</param>
/// <param name="Checksum">Base64 value of that element, or null.</param>
public sealed record S3CompletePart(int PartNumber, string ETag, string? ChecksumAlgorithm, string? Checksum);

/// <summary>A CompleteMultipartUploadResult.</summary>
/// <param name="Checksum">Header-style value (<c>base64-N</c> for a composite checksum), or null.</param>
public sealed record S3CompleteResult(
    string Location, string Bucket, string Key, string ETag, string? ChecksumAlgorithm, string? Checksum, string? ChecksumType);
