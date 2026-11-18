using TimeStore.Application.Abstractions;
using TimeStore.Blob.FileSystem;
using TimeStore.Domain.Blobs;
using TimeStore.Infra.Services;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Blobs;

public sealed class DeferredBlobDeletionsTests : IAsyncLifetime
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-deferred", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileSystemBlobStore _blobs = null!;
    private DeferredBlobDeletions _deletions = null!;

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await _blobs.InitializeAsync(_ct);
        _deletions = new DeferredBlobDeletions(_blobs, new BlobDeletionOptions(Delay), _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _deletions.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<BlobLocator> BlobAsync() => await _blobs.WriteAsync(new MemoryStream([1, 2, 3]), _ct);

    private bool Readable(BlobLocator blob)
    {
        try
        {
            using var stream = _blobs.OpenReadAsync(blob, _ct).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ABlob_StaysReadableAndPending_UntilItsGraceIsOver()
    {
        var blob = await BlobAsync();
        await _deletions.DeferAsync([blob]);

        _clock.Advance(Delay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await _deletions.DeleteDueAsync());
        Assert.True(Readable(blob));
        Assert.True(_deletions.IsPending(blob.BlobId));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await _deletions.DeleteDueAsync());
        Assert.False(Readable(blob));
        Assert.False(_deletions.IsPending(blob.BlobId));
    }

    [Fact]
    public async Task BlobsAreDeletedInTheOrderTheyWereReleased()
    {
        var first = await BlobAsync();
        await _deletions.DeferAsync([first]);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await BlobAsync();
        await _deletions.DeferAsync([second]);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await _deletions.DeleteDueAsync());

        Assert.False(Readable(first));
        Assert.True(Readable(second));
    }

    [Fact]
    public async Task Shutdown_DeletesWhatIsStillPending()
    {
        var blob = await BlobAsync();
        await _deletions.DeferAsync([blob]);

        await _deletions.DisposeAsync();
        _deletions = new DeferredBlobDeletions(_blobs, new BlobDeletionOptions(Delay), _clock); // for DisposeAsync

        Assert.False(Readable(blob));
    }

    [Fact]
    public async Task AZeroDelay_DeletesAtOnce()
    {
        var immediate = new DeferredBlobDeletions(_blobs, new BlobDeletionOptions(TimeSpan.Zero), _clock);
        var blob = await BlobAsync();

        await immediate.DeferAsync([blob]);

        Assert.False(Readable(blob));
        Assert.False(immediate.IsPending(blob.BlobId));
        await immediate.DisposeAsync();
    }

    [Fact]
    public async Task AMissingBlob_IsNotAnError()
    {
        var blob = await BlobAsync();
        await _blobs.DeleteAsync(blob.BlobId, _ct);
        await _deletions.DeferAsync([blob]);
        _clock.Advance(Delay);

        Assert.Equal(1, await _deletions.DeleteDueAsync());
    }

    [Fact]
    public async Task WhileHeld_DueBlobsWait_AndGoWhenTheLastHoldEnds()
    {
        var blob = await BlobAsync();
        await _deletions.DeferAsync([blob]);
        var first = _deletions.Hold();
        var second = _deletions.Hold();

        _clock.Advance(Delay * 2);
        Assert.Equal(0, await _deletions.DeleteDueAsync());
        Assert.True(Readable(blob));
        Assert.True(_deletions.IsPending(blob.BlobId)); // the orphan sweep keeps away from it

        await first.DisposeAsync();
        await first.DisposeAsync(); // releasing twice counts once
        Assert.True(_deletions.IsHeld);
        Assert.True(Readable(blob));

        await second.DisposeAsync();
        Assert.False(_deletions.IsHeld);
        Assert.False(Readable(blob)); // drained by the release itself, not the next tick
        Assert.False(_deletions.IsPending(blob.BlobId));
    }

    [Fact]
    public async Task WhileHeld_AZeroDelay_Queues_UntilTheHoldEnds()
    {
        var immediate = new DeferredBlobDeletions(_blobs, new BlobDeletionOptions(TimeSpan.Zero), _clock);
        var blob = await BlobAsync();
        var hold = immediate.Hold();

        await immediate.DeferAsync([blob]);
        Assert.True(Readable(blob));
        Assert.True(immediate.IsPending(blob.BlobId));

        await hold.DisposeAsync();
        Assert.False(Readable(blob));
        Assert.False(immediate.IsPending(blob.BlobId));
        await immediate.DisposeAsync();
    }
}
