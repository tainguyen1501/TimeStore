using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Maintenance;
using TimeStore.Contracts.Maintenance;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Maintenance;

/// <summary>
/// Lists blobs older than the grace period, asks the metadata store which of them nothing references, deletes those.
/// Safe against concurrent writes: a blob is committed right after its last write, so an old blob without a row will
/// never get one (blob ids are never reused). The metadata store is the only authority on references.
/// </summary>
public sealed class BlobSweepService(
    IBlobStore blobs, IMetadataStore store, IBlobDeletions deletions, BlobSweepOptions options, TimeProvider clock)
    : IBlobSweepService, IDisposable
{
    private const int BatchSize = 500;

    // Single node: an in-process lock is the lease (multi-node: a lease row in the database).
    private readonly SemaphoreSlim _running = new(1, 1);

    public async ValueTask<Result<BlobSweepResponse>> SweepAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(TimeSpan.Zero, ct))
            return MaintenanceErrors.SweepRunning;

        try
        {
            var startedAt = clock.UtcNowMs;
            var cutoff = clock.GetUtcNow().UtcDateTime - options.GracePeriod;
            var scanned = 0;
            var deleted = 0;
            long deletedBytes = 0;
            var batch = new List<BlobFile>(BatchSize);

            await foreach (var file in blobs.ListAsync(cutoff, ct))
            {
                scanned++;
                batch.Add(file);
                if (batch.Count == BatchSize)
                    await DeleteOrphansAsync();
            }

            await DeleteOrphansAsync();
            return new BlobSweepResponse(scanned, deleted, deletedBytes, startedAt, clock.UtcNowMs);

            async ValueTask DeleteOrphansAsync()
            {
                if (batch.Count == 0)
                    return;

                var unreferenced = (await store.FindUnreferencedBlobsAsync(batch.ConvertAll(b => b.BlobId), ct)).ToHashSet();
                foreach (var file in batch)
                {
                    // Pending: released by a commit seconds ago, still readable by GETs in flight; deleted on schedule.
                    if (!unreferenced.Contains(file.BlobId) || deletions.IsPending(file.BlobId))
                        continue;
                    await blobs.DeleteAsync(file.BlobId, ct);
                    deleted++;
                    deletedBytes += file.Size;
                }

                batch.Clear();
            }
        }
        finally
        {
            _running.Release();
        }
    }

    public void Dispose() => _running.Dispose();
}
