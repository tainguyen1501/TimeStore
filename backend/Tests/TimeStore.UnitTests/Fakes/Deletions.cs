using TimeStore.Application.Abstractions;
using TimeStore.Domain.Stores;
using TimeStore.Infra.Services;

namespace TimeStore.UnitTests.Fakes;

/// <summary>
/// Blob deletions for service tests: no wait, so a released blob (recorded by the store) is deleted as soon as the
/// service reports the release, and blob counts are exact right after a call.
/// </summary>
internal static class Deletions
{
    public static IBlobDeletions Immediate(IMetadataStore store, IBlobStore blobs) =>
        new BlobCollector(store, blobs, new BlobDeletionOptions(TimeSpan.Zero), TimeProvider.System);
}
