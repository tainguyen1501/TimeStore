using TimeStore.Application.Abstractions;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services;

/// <summary>
/// Deletes released blobs once their wait is over (<see cref="IBlobDeletions"/>, docs/research/blob-retention.md). The
/// list lives in the metadata (<c>pending_blob_deletions</c>, written by the commits themselves), so nothing is kept in
/// memory: after a crash or a restart the collector goes on where it was, and the orphan sweep never takes a released
/// blob for an orphan. The wait is computed here from the configuration, so raising the retention also protects blobs
/// released before.
/// </summary>
/// <remarks>
/// Also the <see cref="IBlobHold"/>: while a backup holds blobs nothing is collected, and the collector runs when the
/// last hold ends. Every committed blob is deleted here and the sweep skips the released ones, so this is the one place
/// a hold has to stop.
/// </remarks>
public sealed class BlobCollector : IBlobDeletions, IBlobHold, IAsyncDisposable
{
    /// <summary>Released blobs read and deleted per round.</summary>
    public const int BatchSize = 500;

    private readonly IMetadataStore _store;
    private readonly IBlobStore _blobs;
    private readonly TimeProvider _clock;
    private readonly BlobDeletionOptions _options;
    private readonly SemaphoreSlim _collecting = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private int _holds;

    public BlobCollector(IMetadataStore store, IBlobStore blobs, BlobDeletionOptions options, TimeProvider clock)
    {
        _store = store;
        _blobs = blobs;
        _clock = clock;
        _options = options;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Whether a backup holds blobs now (diagnostics, tests).</summary>
    public bool IsHeld => Volatile.Read(ref _holds) > 0;

    public async ValueTask ReleasedAsync()
    {
        if (_options.Wait <= TimeSpan.Zero)
            await CollectAsync();
    }

    public async ValueTask<int> CollectAsync()
    {
        if (IsHeld)
            return 0;
        await _collecting.WaitAsync();
        try
        {
            var deleted = 0;
            var cutoff = _clock.GetUtcNow().UtcDateTime - _options.Wait;
            while (!IsHeld)
            {
                var due = await _store.ListReleasedBlobsAsync(_options.NodeId, cutoff, BatchSize, CancellationToken.None);
                if (due.Count == 0)
                    break;
                // File first, row after: a crash in between leaves a row for a missing file, which deletes quietly.
                foreach (var blob in due)
                    await _blobs.DeleteQuietlyAsync(blob.BlobId);
                await _store.ForgetReleasedBlobsAsync(due.Select(static b => b.BlobId).ToArray(), CancellationToken.None);
                deleted += due.Count;
                if (due.Count < BatchSize)
                    break;
            }

            return deleted;
        }
        finally
        {
            _collecting.Release();
        }
    }

    public IAsyncDisposable Hold()
    {
        Interlocked.Increment(ref _holds);
        return new BlobHoldLease(this);
    }

    private async ValueTask ReleaseHoldAsync()
    {
        if (Interlocked.Decrement(ref _holds) == 0)
            await CollectAsync(); // what waited for the hold goes now
    }

    private sealed class BlobHoldLease(BlobCollector owner) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _released, 1) == 0 ? owner.ReleaseHoldAsync() : ValueTask.CompletedTask;
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_options.Interval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                try
                {
                    await CollectAsync();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The metadata or the disk failed this round; the rows stay and the next tick tries again.
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // shutting down: what waits stays recorded and is collected after the next start
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _loop;
        _stopping.Dispose();
        _collecting.Dispose();
    }
}
