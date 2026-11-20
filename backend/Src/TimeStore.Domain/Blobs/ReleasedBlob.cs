namespace TimeStore.Domain.Blobs;

/// <summary>
/// A blob no metadata row references any more, waiting in <c>pending_blob_deletions</c> until its wait is over
/// (docs/research/blob-retention.md): the read grace, or the retention that keeps older metadata backups restorable.
/// </summary>
/// <param name="ReleasedAt">When the commit that released it happened (the wait counts from here).</param>
public sealed record ReleasedBlob(Guid BlobId, string NodeId, long Size, DateTime ReleasedAt);

/// <summary>Released blobs still on disk: how many and how many bytes (the cost of the retention).</summary>
public readonly record struct RetainedBlobs(long Count, long Bytes);
