using TimeStore.Domain.Blobs;

namespace TimeStore.Domain.Stores;

/// <summary>
/// Consistent copies of the metadata at one instant, taken while the server keeps writing. Implemented by the metadata
/// provider; a backup archives the copy and every blob it references.
/// </summary>
public interface IMetadataSnapshots
{
    /// <summary>
    /// Copies the metadata as it is now (instant T) into a temporary file owned by the returned snapshot; disposing it
    /// deletes the file. Admin sessions and backup history are left out of the copy.
    /// </summary>
    ValueTask<IMetadataSnapshot> CreateAsync(Guid runId, CancellationToken ct);
}

public interface IMetadataSnapshot : IAsyncDisposable
{
    /// <summary>What the copy holds (counted once, when it was taken).</summary>
    MetadataSnapshotSummary Summary { get; }

    /// <summary>Every blob the copy references (current and noncurrent versions, parts of open uploads), each once.</summary>
    IAsyncEnumerable<BlobLocator> ListBlobsAsync(CancellationToken ct);

    /// <summary>The database file of the copy, to archive.</summary>
    Stream OpenRead();
}

/// <param name="Bytes">Size of the referenced blobs, each counted once.</param>
/// <param name="DatabaseBytes">Size of the database file of the copy.</param>
public sealed record MetadataSnapshotSummary(
    int SchemaVersion, long Buckets, long Objects, long Versions, long Blobs, long Bytes, long DatabaseBytes);
