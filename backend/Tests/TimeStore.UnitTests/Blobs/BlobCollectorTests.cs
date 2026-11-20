using Microsoft.Data.Sqlite;
using TimeStore.Application.Abstractions;
using TimeStore.Blob.FileSystem;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Infra.Services;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Blobs;

/// <summary>
/// Released blobs are recorded by the commit (SQLite, <c>pending_blob_deletions</c>) and deleted by the collector once
/// their wait is over: the read grace, or the retention for restores of older metadata (docs/research/blob-retention.md).
/// </summary>
public sealed class BlobCollectorTests : IAsyncLifetime
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-collector", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileSystemBlobStore _blobs = null!;
    private SqliteMetadataStore _store = null!;
    private Bucket _bucket = null!;
    private readonly List<BlobCollector> _collectors = [];

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(Path.Combine(_root, "data"), "node-1"));
        await _blobs.InitializeAsync(_ct);
        var connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_root, "metadata.db")));
        await new SqliteMetadataInitializer(connections, TimeProvider.System).InitializeAsync(_ct);
        _store = new SqliteMetadataStore(connections, _clock);
        _bucket = new Bucket(Guid.CreateVersion7(), "photos", _clock.GetUtcNow().UtcDateTime);
        Assert.True(await _store.TryCreateBucketAsync(_bucket, _ct));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var collector in _collectors)
            await collector.DisposeAsync();
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private BlobCollector Collector(TimeSpan delay, TimeSpan retention = default, string nodeId = "node-1")
    {
        var collector = new BlobCollector(_store, _blobs, new BlobDeletionOptions(delay, retention, TimeSpan.FromHours(1), nodeId), _clock);
        _collectors.Add(collector);
        return collector;
    }

    /// <summary>Puts the key, then overwrites it: returns the blob the overwrite released.</summary>
    private async Task<BlobLocator> ReleaseAsync(string key = "a")
    {
        var first = await PutAsync(key);
        await PutAsync(key);
        return first;
    }

    private async Task<BlobLocator> PutAsync(string key)
    {
        var blob = await _blobs.WriteAsync(new MemoryStream([1, 2, 3]), _ct);
        var stored = new StoredObject(_bucket.Id, key, Guid.CreateVersion7(), new SingleBlob(blob), blob.Md5Hex,
            StoredObject.DefaultContentType, null, null, null, [], [], _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(CommitStatus.Committed, (await _store.CommitObjectAsync(stored, CommitCondition.Always, _ct)).Status);
        return blob;
    }

    private bool Exists(BlobLocator blob)
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
    public async Task AReleasedBlob_StaysReadable_UntilItsGraceIsOver()
    {
        var collector = Collector(Delay);
        var blob = await ReleaseAsync();
        Assert.Equal(new RetainedBlobs(1, 3), await _store.GetRetainedBlobsAsync(_ct));

        _clock.Advance(Delay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await collector.CollectAsync());
        Assert.True(Exists(blob));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await collector.CollectAsync());
        Assert.False(Exists(blob));
        Assert.Equal(default, await _store.GetRetainedBlobsAsync(_ct));
    }

    [Fact]
    public async Task Retention_KeepsReleasedBlobs_ForRestoresOfOlderMetadata()
    {
        var collector = Collector(Delay, retention: TimeSpan.FromDays(35));
        var blob = await ReleaseAsync();

        _clock.Advance(TimeSpan.FromDays(34));
        Assert.Equal(0, await collector.CollectAsync());
        Assert.True(Exists(blob));

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await collector.CollectAsync());
        Assert.False(Exists(blob));
    }

    [Fact]
    public async Task RaisingTheRetention_AlsoProtectsBlobsReleasedBefore_AndRestartsForgetNothing()
    {
        var blob = await ReleaseAsync(); // released while the server ran with the 60 s grace only
        _clock.Advance(TimeSpan.FromMinutes(5));

        // Restarted with a retention (a new collector over the same database): the wait counts from the release.
        var restarted = Collector(Delay, retention: TimeSpan.FromDays(7));
        Assert.Equal(0, await restarted.CollectAsync());
        Assert.True(Exists(blob));
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, await restarted.CollectAsync());
    }

    [Fact]
    public async Task AHold_StopsCollection_AndItsEndCollectsWhatWaited()
    {
        var collector = Collector(Delay);
        var blob = await ReleaseAsync();
        _clock.Advance(Delay);

        var hold = collector.Hold();
        Assert.Equal(0, await collector.CollectAsync());
        Assert.True(Exists(blob));

        await hold.DisposeAsync();
        Assert.False(Exists(blob));
    }

    [Fact]
    public async Task WithNoWait_AReportedReleaseIsDeletedAtOnce()
    {
        var collector = Collector(TimeSpan.Zero);
        var blob = await ReleaseAsync();

        await collector.ReleasedAsync();

        Assert.False(Exists(blob));
    }

    [Fact]
    public async Task ANode_CollectsOnlyBlobsOnItsOwnDisk()
    {
        var other = Collector(Delay, nodeId: "node-2");
        var blob = await ReleaseAsync();
        _clock.Advance(Delay);

        Assert.Equal(0, await other.CollectAsync());
        Assert.True(Exists(blob));
    }
}
