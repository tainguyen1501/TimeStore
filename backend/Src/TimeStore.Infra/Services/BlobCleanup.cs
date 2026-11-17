using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services;

/// <summary>
/// Blob deletion after the commit point (or after a failed write) is best effort: a failure leaves an orphan the sweep
/// removes later, never a failed request for data that is already committed.
/// </summary>
internal static class BlobCleanup
{
    extension(IBlobStore blobs)
    {
        public async ValueTask DeleteQuietlyAsync(IEnumerable<BlobLocator> locators)
        {
            foreach (var blob in locators)
                await blobs.DeleteQuietlyAsync(blob);
        }

        public async ValueTask DeleteQuietlyAsync(BlobLocator blob)
        {
            try
            {
                await blobs.DeleteAsync(blob.BlobId, CancellationToken.None);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
