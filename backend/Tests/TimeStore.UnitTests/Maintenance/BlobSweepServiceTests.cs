using System.Runtime.CompilerServices;
using TimeStore.Application.Features.Maintenance;
using TimeStore.Application.Features.Objects;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Buckets;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;
using TimeStore.Infra.Services.Buckets;
using TimeStore.Infra.Services.Maintenance;
using TimeStore.Infra.Services.Objects;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Maintenance;

public sealed class BlobSweepServiceTests : IAsyncLifetime
{
    private const string Bucket = "photos";
    private static readonly TimeSpan Grace = TimeSpan.FromHours(1);
    private static readonly DateTime Old = DateTime.UtcNow - TimeSpan.FromHours(2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-sweep", Guid.NewGuid().ToString("N"));
    private readonly InMemoryMetadataStore _store = new();
    private readonly FixedTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileSystemBlobStore _blobs = null!;
    private BlobSweepService _sweeps = null!;

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await _blobs.InitializeAsync(_ct);
        _sweeps = new BlobSweepService(_blobs, _store, Deletions.Immediate(_blobs), new BlobSweepOptions(Grace), _clock);
        await new BucketService(_store, Deletions.Immediate(_blobs), new TimeStore.Infra.Services.Policies.BucketPolicyCache(_store), _clock).CreateAsync(new CreateBucketRequest(Bucket), _ct);
    }

    public ValueTask DisposeAsync()
    {
        _sweeps.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private string PathOf(Guid blobId)
    {
        var name = blobId.ToString("N");
        return Path.Combine(_root, FileSystemBlobStore.BlobsDirectory, name[^2..], name);
    }

    private void Age(Guid blobId) => File.SetLastWriteTimeUtc(PathOf(blobId), Old);

    /// <summary>A blob written but never committed: what a crash between write and commit leaves.</summary>
    private async Task<BlobLocator> CrashedWriteAsync(int size = 10)
    {
        var blob = await _blobs.WriteAsync(new MemoryStream(new byte[size]), _ct);
        Age(blob.BlobId);
        return blob;
    }

    [Fact]
    public async Task Sweep_DeletesAnOldBlobNoRowReferences()
    {
        var orphan = await CrashedWriteAsync(size: 10);

        var report = (await _sweeps.SweepAsync(_ct)).Value;

        Assert.False(File.Exists(PathOf(orphan.BlobId)));
        Assert.Equal((1, 1, 10L), (report.Scanned, report.Deleted, report.DeletedBytes));
    }

    [Fact]
    public async Task Sweep_LeavesBlobsWaitingForTheirDeletionGrace()
    {
        // An overwrite released this blob seconds ago; a GET may still be reading it. Its file is old (written long
        // ago) and no row references it, but it is the deletion queue's to delete, after its grace.
        var released = await CrashedWriteAsync();
        var deletions = new TimeStore.Infra.Services.DeferredBlobDeletions(
            _blobs, new TimeStore.Application.Abstractions.BlobDeletionOptions(TimeSpan.FromSeconds(60)), _clock);
        await deletions.DeferAsync([released]);
        using var sweeps = new BlobSweepService(_blobs, _store, deletions, new BlobSweepOptions(Grace), _clock);

        var report = (await sweeps.SweepAsync(_ct)).Value;

        Assert.True(File.Exists(PathOf(released.BlobId)));
        Assert.Equal((1, 0), (report.Scanned, report.Deleted));
        await deletions.DisposeAsync();
    }

    [Fact]
    public async Task Sweep_KeepsCommittedBlobs_EvenWhenOld()
    {
        var service = new ObjectService(_store, _blobs, Deletions.Immediate(_blobs), _clock);
        await service.PutAsync(new PutObjectCommand(Bucket, "a.txt", 3, "text/plain", null, [], [], false),
            new MemoryStream("abc"u8.ToArray()), () => null, _ct);
        var committed = ((SingleBlob)(await service.HeadAsync(Bucket, "a.txt", _ct)).Value.Data).Blob;
        Age(committed.BlobId);

        var report = (await _sweeps.SweepAsync(_ct)).Value;

        Assert.True(File.Exists(PathOf(committed.BlobId)));
        Assert.Equal((1, 0), (report.Scanned, report.Deleted));
    }

    [Fact]
    public async Task Sweep_KeepsBlobsInsideTheGracePeriod()
    {
        var fresh = await _blobs.WriteAsync(new MemoryStream(new byte[5]), _ct); // commit may still be in flight

        var report = (await _sweeps.SweepAsync(_ct)).Value;

        Assert.True(File.Exists(PathOf(fresh.BlobId)));
        Assert.Equal(0, report.Scanned);
    }

    [Fact]
    public async Task Sweep_NeverTouchesFilesThatAreNotBlobs()
    {
        var id = Guid.CreateVersion7().ToString("N");
        var blobs = Path.Combine(_root, FileSystemBlobStore.BlobsDirectory);
        string[] strangers =
        [
            Path.Combine(blobs, id[^2..], "notes.txt"),
            Path.Combine(blobs, id[^2..], id.ToUpperInvariant()), // not the canonical name
            Path.Combine(blobs, id[^2..] == "00" ? "01" : "00", id), // wrong shard
            Path.Combine(blobs, id), // outside any shard
        ];
        foreach (var path in strangers)
        {
            await File.WriteAllTextAsync(path, "keep me", _ct);
            File.SetLastWriteTimeUtc(path, Old);
        }

        var report = (await _sweeps.SweepAsync(_ct)).Value;

        Assert.All(strangers, path => Assert.True(File.Exists(path), path));
        Assert.Equal(0, report.Scanned);
    }

    [Fact]
    public async Task Sweep_HandlesMoreThanOneBatch()
    {
        for (var i = 0; i < 1100; i++)
        {
            var path = PathOf(Guid.CreateVersion7());
            await File.WriteAllBytesAsync(path, [1], _ct);
            File.SetLastWriteTimeUtc(path, Old);
        }

        var report = (await _sweeps.SweepAsync(_ct)).Value;

        Assert.Equal((1100, 1100, 1100L), (report.Scanned, report.Deleted, report.DeletedBytes));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Sweep_WhileAnotherRuns_IsRefused()
    {
        var gate = new TaskCompletionSource();
        using var sweeps = new BlobSweepService(new BlockingBlobStore(gate.Task), _store, Deletions.Immediate(_blobs), new BlobSweepOptions(Grace), _clock);
        var first = sweeps.SweepAsync(_ct).AsTask();

        var second = await sweeps.SweepAsync(_ct);
        gate.SetResult();

        Assert.Equal(MaintenanceErrors.Codes.SweepRunning, second.Error.Code);
        Assert.True((await first).IsSuccess);
        Assert.True((await sweeps.SweepAsync(_ct)).IsSuccess); // the lock is released afterwards
    }

    /// <summary>Lists nothing, but only once <paramref name="gate"/> completes.</summary>
    private sealed class BlockingBlobStore(Task gate) : IBlobStore
    {
        public async IAsyncEnumerable<BlobFile> ListAsync(DateTime writtenBefore, [EnumeratorCancellation] CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            yield break;
        }

        public ValueTask<BlobLocator> WriteAsync(Stream source, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(BlobLocator blob, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid blobId, CancellationToken ct) => throw new NotSupportedException();
    }
}
