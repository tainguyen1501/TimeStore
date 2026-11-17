namespace TimeStore.Contracts.Objects;

/// <summary>
/// One page of the object browser: the folders (common prefixes under <c>/</c>) and objects directly in
/// <paramref name="Prefix"/>. <paramref name="NextStartAfter"/> continues the listing when it is truncated.
/// </summary>
public sealed record ObjectListResponse(
    string Prefix,
    IReadOnlyList<FolderResponse> Folders,
    IReadOnlyList<ObjectItemResponse> Objects,
    bool IsTruncated,
    string? NextStartAfter);

/// <param name="Prefix">The folder's full prefix, ending with <c>/</c>.</param>
public sealed record FolderResponse(string Prefix);

/// <param name="ETag">Without quotes.</param>
/// <param name="VersionId">Null until versioning (roadmap 6a).</param>
public sealed record ObjectItemResponse(string Key, long Size, string ETag, DateTime LastModified, string? VersionId);

public sealed record ObjectHeaderResponse(string Name, string Value);

/// <param name="Checksum">Base64 digest of <paramref name="ChecksumAlgorithm"/>, or null.</param>
/// <param name="Headers">Stored system headers (Cache-Control, Content-Disposition, ...).</param>
/// <param name="UserMetadata"><c>x-amz-meta-*</c> without the prefix.</param>
public sealed record ObjectDetailsResponse(
    string Key,
    long Size,
    string ETag,
    string ContentType,
    DateTime LastModified,
    string? VersionId,
    string? ChecksumAlgorithm,
    string? Checksum,
    string? ChecksumType,
    int? PartsCount,
    IReadOnlyList<ObjectHeaderResponse> Headers,
    IReadOnlyList<ObjectHeaderResponse> UserMetadata);

/// <summary>An object written by an upload (single request or completed multipart upload).</summary>
public sealed record ObjectWrittenResponse(string Key, string ETag, string? VersionId);

/// <param name="Keys">Objects to delete: their latest version (a delete marker in a versioned bucket).</param>
/// <param name="Prefixes">Folders to delete with everything under them (each ends with <c>/</c>, never empty).</param>
/// <param name="Versions">Exact versions or delete markers to remove permanently.</param>
public sealed record DeleteObjectsRequest(
    IReadOnlyList<string>? Keys, IReadOnlyList<string>? Prefixes, IReadOnlyList<ObjectVersionRef>? Versions = null);

public sealed record ObjectVersionRef(string Key, string VersionId);

/// <summary>One version or delete marker in the object browser's versions view.</summary>
public sealed record ObjectVersionItemResponse(
    string Key, string VersionId, bool IsLatest, bool IsDeleteMarker, long Size, string ETag, DateTime LastModified);

/// <summary>Folders and every version (and delete marker) of the objects directly in <paramref name="Prefix"/>.</summary>
public sealed record ObjectVersionListResponse(
    string Prefix,
    IReadOnlyList<FolderResponse> Folders,
    IReadOnlyList<ObjectVersionItemResponse> Versions,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextVersionIdMarker);

/// <summary>Restore: copy <paramref name="VersionId"/> of <paramref name="Key"/> over the key, as a new latest version.</summary>
public sealed record RestoreVersionRequest(string Key, string VersionId);

public sealed record DeleteObjectsResponse(int Deleted);

public sealed record StartUploadRequest(string Key, string? ContentType);

public sealed record StartUploadResponse(string UploadId);

/// <param name="ETag">Without quotes; sent back when completing.</param>
public sealed record UploadedPartResponse(int PartNumber, string ETag);

public sealed record CompleteUploadRequest(IReadOnlyList<UploadedPartResponse> Parts);

/// <summary>
/// How an object is reached over S3: its URL on the S3 port, and whether anyone may read it without signing (a bucket
/// policy allows <c>s3:GetObject</c> to <c>*</c> without conditions).
/// </summary>
public sealed record ObjectAccessResponse(string Url, bool IsPublic);

/// <param name="ExpiresInSeconds">1 second to 7 days (604 800); the UI offers 1 hour, 1 day and 7 days.</param>
public sealed record ShareObjectRequest(string Key, string? VersionId, int ExpiresInSeconds);

/// <summary>A presigned GET URL, signed with the signed-in key: it stops working when it expires or when that key is disabled or deleted.</summary>
public sealed record ShareObjectResponse(string Url, DateTime ExpiresAt, string SignedBy);
