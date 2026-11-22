using TimeStore.Domain.Blobs;

namespace TimeStore.Domain.Stores;

/// <summary>
/// Reads a metadata database file that no server has open (a restore, with the server stopped): which blobs it
/// references. Implemented by the metadata provider.
/// </summary>
public interface IMetadataFiles
{
    /// <summary>
    /// Every blob the database at <paramref name="path"/> references (current objects, versions, parts of multipart
    /// objects and of open uploads), with the bucket and key that use it, read in one pass.
    /// </summary>
    IAsyncEnumerable<ReferencedBlob> ListReferencedBlobsAsync(string path, CancellationToken ct);
}

/// <param name="Bucket">The bucket's name; <paramref name="Key"/> the object (or upload) that references the blob.</param>
public sealed record ReferencedBlob(Guid BlobId, long Size, string Bucket, string Key);
