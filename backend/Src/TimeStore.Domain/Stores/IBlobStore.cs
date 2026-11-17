using TimeStore.Domain.Blobs;

namespace TimeStore.Domain.Stores;

/// <summary>
/// Blob storage. A blob is written once and never changed. Write order in services: blob first (durable), then
/// the metadata commit; a crash in between leaves an orphan blob that the orphan sweep removes, never a dangling
/// metadata row.
/// </summary>
public interface IBlobStore
{
    /// <summary>Streams <paramref name="source"/> into a new blob and makes it durable before returning.</summary>
    ValueTask<BlobLocator> WriteAsync(Stream source, CancellationToken ct);

    /// <summary>Opens a committed blob for reading. The caller disposes the stream.</summary>
    ValueTask<Stream> OpenReadAsync(BlobLocator blob, CancellationToken ct);

    /// <summary>Removes one of this store's blobs; no error when it is already gone.</summary>
    ValueTask DeleteAsync(Guid blobId, CancellationToken ct);

    /// <summary>
    /// This store's committed blobs last written before <paramref name="writtenBefore"/> (orphan sweep). Lists only
    /// files that are blobs by name and place; anything else in the directory is never reported, so never deleted.
    /// </summary>
    IAsyncEnumerable<BlobFile> ListAsync(DateTime writtenBefore, CancellationToken ct);
}
