using System.Collections.Concurrent;
using TimeStore.Application.Abstractions;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services;

/// <summary>
/// <see cref="IBlobDeletions"/> in memory: a FIFO of released blobs (the delay is the same for all, so the queue is in
/// due order) drained once a second. On shutdown the remaining ones are deleted at once (no request reads anymore);
/// after a crash the orphan sweep finds them (no row references them).
/// </summary>
public sealed class DeferredBlobDeletions : IBlobDeletions, IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly IBlobStore _blobs;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _delay;
    private readonly ConcurrentQueue<(Guid BlobId, BlobLocator Blob, DateTimeOffset Due)> _queue = new();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    public DeferredBlobDeletions(IBlobStore blobs, BlobDeletionOptions options, TimeProvider clock)
    {
        _blobs = blobs;
        _clock = clock;
        _delay = options.Delay;
        _loop = _delay > TimeSpan.Zero ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    public async ValueTask DeferAsync(IEnumerable<BlobLocator> blobs)
    {
        if (_delay <= TimeSpan.Zero)
        {
            await _blobs.DeleteQuietlyAsync(blobs);
            return;
        }

        var due = _clock.GetUtcNow() + _delay;
        foreach (var blob in blobs)
        {
            _pending[blob.BlobId] = 0; // before the queue: the sweep must never see it as free
            _queue.Enqueue((blob.BlobId, blob, due));
        }
    }

    public bool IsPending(Guid blobId) => _pending.ContainsKey(blobId);

    /// <summary>Deletes every blob whose grace period is over; returns how many (tests and the loop).</summary>
    public async ValueTask<int> DeleteDueAsync()
    {
        var now = _clock.GetUtcNow();
        var deleted = 0;
        while (_queue.TryPeek(out var next) && next.Due <= now && _queue.TryDequeue(out var item))
        {
            await _blobs.DeleteQuietlyAsync(item.Blob);
            _pending.TryRemove(item.BlobId, out _);
            deleted++;
        }

        return deleted;
    }

    /// <summary>Waiting blobs (diagnostics, tests).</summary>
    public int PendingCount => _pending.Count;

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Tick, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
                await DeleteDueAsync();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // shutting down
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _loop;
        while (_queue.TryDequeue(out var item))
        {
            await _blobs.DeleteQuietlyAsync(item.Blob);
            _pending.TryRemove(item.BlobId, out _);
        }

        _stopping.Dispose();
    }
}
