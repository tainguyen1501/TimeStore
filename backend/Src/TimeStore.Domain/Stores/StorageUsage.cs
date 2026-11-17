namespace TimeStore.Domain.Stores;

/// <summary>
/// One bucket: current objects and their bytes, noncurrent versions and delete markers and their bytes (incomplete
/// uploads not included).
/// </summary>
public sealed record BucketUsage(
    Guid BucketId, string BucketName, long ObjectCount, long TotalBytes, long VersionCount = 0, long VersionBytes = 0);

/// <summary>Multipart uploads in progress and the bytes their parts hold.</summary>
public sealed record UploadUsage(long Uploads, long Parts, long PartBytes);
