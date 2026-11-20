using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Uploads;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services.Uploads;

public sealed class UploadExpiryService(
    IMetadataStore store, IBlobDeletions deletions, UploadExpiryOptions options, TimeProvider clock) : IUploadExpiryService
{
    private const int BatchSize = 100;

    public async ValueTask<int> AbortAbandonedAsync(CancellationToken ct)
    {
        if (options.AbandonAfter is not { } age)
            return 0;

        var cutoff = clock.GetUtcNow().UtcDateTime - age;
        var aborted = 0;
        while (true)
        {
            var batch = await store.ListUploadsCreatedBeforeAsync(cutoff, BatchSize, ct);
            foreach (var upload in batch)
            {
                // Null: completed or aborted by a client since the listing; nothing to free.
                if (await store.AbortUploadAsync(upload.BucketId, upload.Key, upload.UploadId, ct) is { } parts)
                {
                    await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
                    aborted++;
                }
            }

            if (batch.Count < BatchSize)
                return aborted;
        }
    }
}
