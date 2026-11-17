namespace TimeStore.Protocol.Routing;

/// <summary>
/// Every S3 request the router recognises. Closed hierarchies: a dispatcher <c>switch</c> over
/// <see cref="S3Operation"/> must name every case, so adding an operation breaks the build until it is handled.
/// Names and parameters follow the Amazon S3 API reference.
/// </summary>
public closed record S3Operation;

/// <summary><c>GET /</c>.</summary>
public sealed record ListBuckets : S3Operation;

/// <summary>
/// A known S3 feature TimeStore does not implement (a sub-resource such as <c>?acl</c>, or browser POST uploads).
/// Answered with <c>NotImplemented</c> instead of being mistaken for a plain object or bucket operation.
/// </summary>
public sealed record UnsupportedOperation(string? Bucket, string? Key, string Feature) : S3Operation;

/// <summary>Operations on a bucket (<c>/bucket</c>).</summary>
public closed record BucketOperation(string Bucket) : S3Operation;

public sealed record CreateBucket(string Bucket) : BucketOperation(Bucket);

public sealed record DeleteBucket(string Bucket) : BucketOperation(Bucket);

public sealed record HeadBucket(string Bucket) : BucketOperation(Bucket);

public sealed record GetBucketLocation(string Bucket) : BucketOperation(Bucket);

public sealed record GetBucketVersioning(string Bucket) : BucketOperation(Bucket);

public sealed record PutBucketVersioning(string Bucket) : BucketOperation(Bucket);

public sealed record GetBucketLifecycle(string Bucket) : BucketOperation(Bucket);

public sealed record PutBucketLifecycle(string Bucket) : BucketOperation(Bucket);

public sealed record DeleteBucketLifecycle(string Bucket) : BucketOperation(Bucket);

public sealed record GetBucketPolicy(string Bucket) : BucketOperation(Bucket);

public sealed record PutBucketPolicy(string Bucket) : BucketOperation(Bucket);

public sealed record DeleteBucketPolicy(string Bucket) : BucketOperation(Bucket);

public sealed record GetBucketPolicyStatus(string Bucket) : BucketOperation(Bucket);

/// <summary><c>POST /bucket?delete</c> (multi-object delete; keys are in the XML body).</summary>
public sealed record DeleteObjects(string Bucket) : BucketOperation(Bucket);

/// <summary><c>GET /bucket</c> (version 1 listing).</summary>
public sealed record ListObjects(
    string Bucket, string? Prefix, string? Delimiter, string? Marker, int MaxKeys, bool EncodeUrl)
    : BucketOperation(Bucket);

/// <summary><c>GET /bucket?list-type=2</c>.</summary>
public sealed record ListObjectsV2(
    string Bucket, string? Prefix, string? Delimiter, string? ContinuationToken, string? StartAfter, int MaxKeys,
    bool EncodeUrl, bool FetchOwner)
    : BucketOperation(Bucket);

/// <summary><c>GET /bucket?versions</c>.</summary>
public sealed record ListObjectVersions(
    string Bucket, string? Prefix, string? Delimiter, string? KeyMarker, string? VersionIdMarker, int MaxKeys,
    bool EncodeUrl)
    : BucketOperation(Bucket);

/// <summary><c>GET /bucket?uploads</c>.</summary>
public sealed record ListMultipartUploads(
    string Bucket, string? Prefix, string? Delimiter, string? KeyMarker, string? UploadIdMarker, int MaxUploads,
    bool EncodeUrl)
    : BucketOperation(Bucket);

/// <summary>Operations on an object (<c>/bucket/key</c>). <see cref="Key"/> is the decoded UTF-8 key.</summary>
public closed record ObjectOperation(string Bucket, string Key) : S3Operation;

/// <param name="ResponseHeaders">
/// Response header overrides from <c>response-*</c> query parameters (presigned URLs): header name → value.
/// </param>
public sealed record GetObject(
    string Bucket, string Key, string? VersionId, int? PartNumber, IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders)
    : ObjectOperation(Bucket, Key);

public sealed record HeadObject(string Bucket, string Key, string? VersionId, int? PartNumber)
    : ObjectOperation(Bucket, Key);

public sealed record PutObject(string Bucket, string Key) : ObjectOperation(Bucket, Key);

/// <param name="CopySource">Raw <c>x-amz-copy-source</c> header (parsed by the copy handler).</param>
public sealed record CopyObject(string Bucket, string Key, string CopySource) : ObjectOperation(Bucket, Key);

public sealed record DeleteObject(string Bucket, string Key, string? VersionId) : ObjectOperation(Bucket, Key);

public sealed record CreateMultipartUpload(string Bucket, string Key) : ObjectOperation(Bucket, Key);

public sealed record UploadPart(string Bucket, string Key, string UploadId, int PartNumber)
    : ObjectOperation(Bucket, Key);

public sealed record UploadPartCopy(string Bucket, string Key, string UploadId, int PartNumber, string CopySource)
    : ObjectOperation(Bucket, Key);

public sealed record CompleteMultipartUpload(string Bucket, string Key, string UploadId) : ObjectOperation(Bucket, Key);

public sealed record AbortMultipartUpload(string Bucket, string Key, string UploadId) : ObjectOperation(Bucket, Key);

public sealed record ListParts(string Bucket, string Key, string UploadId, int MaxParts, int? PartNumberMarker)
    : ObjectOperation(Bucket, Key);
