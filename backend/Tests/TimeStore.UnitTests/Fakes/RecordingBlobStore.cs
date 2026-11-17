using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;

namespace TimeStore.UnitTests.Fakes;

/// <summary>A blob store that only records deletions (for services that never read or write blobs themselves).</summary>
internal sealed class RecordingBlobStore : IBlobStore
{
    public List<Guid> Deleted { get; } = [];

    public ValueTask DeleteAsync(Guid blobId, CancellationToken ct)
    {
        lock (Deleted)
            Deleted.Add(blobId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<BlobLocator> WriteAsync(Stream source, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<Stream> OpenReadAsync(BlobLocator blob, CancellationToken ct) => throw new NotSupportedException();

    public IAsyncEnumerable<BlobFile> ListAsync(DateTime writtenBefore, CancellationToken ct) => throw new NotSupportedException();
}
