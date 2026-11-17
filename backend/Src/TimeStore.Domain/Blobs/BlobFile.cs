namespace TimeStore.Domain.Blobs;

/// <summary>A blob as the blob store sees it (no metadata): what the orphan sweep compares with the database.</summary>
/// <param name="WrittenAt">Last write (UTC). A blob is committed right after its last write, so age is measured here.</param>
public sealed record BlobFile(Guid BlobId, long Size, DateTime WrittenAt);
