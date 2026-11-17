using TimeStore.Application.Abstractions;
using TimeStore.Domain.Stores;
using TimeStore.Infra.Services;

namespace TimeStore.UnitTests.Fakes;

/// <summary>Blob deletions for service tests: at once (delay zero), so blob counts are exact right after a call.</summary>
internal static class Deletions
{
    public static IBlobDeletions Immediate(IBlobStore blobs) =>
        new DeferredBlobDeletions(blobs, new BlobDeletionOptions(TimeSpan.Zero), TimeProvider.System);
}
