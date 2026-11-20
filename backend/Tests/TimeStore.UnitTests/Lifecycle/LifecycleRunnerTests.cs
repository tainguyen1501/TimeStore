using Microsoft.Data.Sqlite;
using TimeStore.Application.Abstractions;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Lifecycle;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Uploads;
using TimeStore.Infra.Services.Lifecycle;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Lifecycle;

/// <summary>Step 6c: lifecycle rules applied by the runner, on a real SQLite store and a clock moved by hand.</summary>
public sealed class LifecycleRunnerTests : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Start));
    private readonly RecordingDeletions _deletions = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private SqliteMetadataStore _store = null!;
    private LifecycleRunner _runner = null!;
    private Bucket _bucket = null!;

    public async ValueTask InitializeAsync()
    {
        var connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        await new SqliteMetadataInitializer(connections, TimeProvider.System).InitializeAsync(_ct);
        _store = new SqliteMetadataStore(connections, _clock); // deletes record noncurrent time on the same clock
        _runner = new LifecycleRunner(_store, _deletions, _clock);
        _bucket = new Bucket(Guid.CreateVersion7(), "lc-bucket", Start);
        await _store.TryCreateBucketAsync(_bucket, _ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task PutAsync(string key, long size = 10)
    {
        var blob = new BlobLocator(Guid.CreateVersion7(), "node-1", size, "d41d8cd98f00b204e9800998ecf8427e");
        var stored = new StoredObject(_bucket.Id, key, Guid.CreateVersion7(), new SingleBlob(blob), blob.Md5Hex,
            StoredObject.DefaultContentType, null, null, null, [], [], Now);
        await _store.CommitObjectAsync(stored, CommitCondition.Always, _ct);
    }

    private Task SetRulesAsync(params LifecycleRule[] rules) => _store.SetLifecycleAsync(_bucket.Id, rules, _ct).AsTask();

    private static LifecycleRule Rule(
        string prefix = "", int? days = null, DateTime? date = null, bool markers = false, int? noncurrentDays = null,
        int? keepNewer = null, int? abortDays = null, long? larger = null) =>
        new($"r{Guid.NewGuid():N}", true, new LifecycleFilter(prefix, larger, null), days, date, markers, noncurrentDays, keepNewer, abortDays);

    private async Task<List<string>> CurrentKeysAsync() =>
        (await _store.ListObjectsAsync(_bucket.Id, "", null, null, 1000, _ct)).Objects.Select(o => o.Key).ToList();

    private async Task<List<VersionSummary>> VersionsAsync() =>
        (await _store.ListObjectVersionsAsync(_bucket.Id, "", null, null, null, 1000, _ct)).Versions.ToList();

    [Fact]
    public async Task ExpirationDays_RoundsToTheNextMidnightUtc_AndFollowsThePrefix()
    {
        await PutAsync("logs/a"); // created 10:00 on day 0
        await PutAsync("keep/b");
        await SetRulesAsync(Rule("logs/", days: 1));

        _clock.Advance(TimeSpan.FromHours(37)); // day 1, 23:00: creation + 1 day is not yet past a midnight
        Assert.Equal(0, (await _runner.RunAsync(_ct)).Expired);

        _clock.Advance(TimeSpan.FromHours(1)); // midnight starting day 2
        var run = await _runner.RunAsync(_ct);

        Assert.Equal(1, run.Expired);
        Assert.Equal(["keep/b"], await CurrentKeysAsync());
        Assert.Equal(1L, (await _store.GetRetainedBlobsAsync(_ct)).Count); // the expired object's blob, after the usual grace
    }

    [Fact]
    public async Task ExpirationDate_AppliesFromThatMidnight()
    {
        await PutAsync("a");
        await SetRulesAsync(Rule(date: Start.Date.AddDays(3)));

        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, (await _runner.RunAsync(_ct)).Expired);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await _runner.RunAsync(_ct)).Expired);
    }

    [Fact]
    public async Task SizeFilters_Apply()
    {
        await PutAsync("small", size: 5);
        await PutAsync("large", size: 5000);
        await SetRulesAsync(Rule(days: 1, larger: 1000));

        _clock.Advance(TimeSpan.FromDays(3));
        await _runner.RunAsync(_ct);

        Assert.Equal(["small"], await CurrentKeysAsync());
    }

    [Fact]
    public async Task InAVersionedBucket_ExpirationAddsADeleteMarker_NoncurrentDaysThenRemoveTheData()
    {
        await _store.SetBucketVersioningAsync(_bucket.Name, BucketVersioning.Enabled, _ct);
        _bucket = (await _store.FindBucketAsync(_bucket.Name, _ct))!;
        await PutAsync("doc");
        await SetRulesAsync(Rule(days: 1, noncurrentDays: 2));

        _clock.Advance(TimeSpan.FromDays(2)); // the object expires: a marker, the data stays
        var first = await _runner.RunAsync(_ct);
        Assert.Equal((1, 0), (first.Expired, first.NoncurrentRemoved));
        Assert.Empty(await CurrentKeysAsync());
        Assert.Equal([true, false], (await VersionsAsync()).Select(v => v.IsDeleteMarker));
        Assert.Equal(0L, (await _store.GetRetainedBlobsAsync(_ct)).Count);

        _clock.Advance(TimeSpan.FromDays(3)); // noncurrent for two full days: removed for good
        var second = await _runner.RunAsync(_ct);
        Assert.Equal(1, second.NoncurrentRemoved);
        Assert.True(Assert.Single(await VersionsAsync()).IsDeleteMarker);
        Assert.Equal(1L, (await _store.GetRetainedBlobsAsync(_ct)).Count);
    }

    [Fact]
    public async Task NewerNoncurrentVersions_AreKept()
    {
        await _store.SetBucketVersioningAsync(_bucket.Name, BucketVersioning.Enabled, _ct);
        for (var i = 0; i < 4; i++)
        {
            await PutAsync("v");
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        await SetRulesAsync(Rule(noncurrentDays: 1, keepNewer: 2));
        _clock.Advance(TimeSpan.FromDays(2));
        var run = await _runner.RunAsync(_ct);

        Assert.Equal(1, run.NoncurrentRemoved); // 3 noncurrent, the newest 2 kept
        Assert.Equal(3, (await VersionsAsync()).Count);
    }

    [Fact]
    public async Task ALoneDeleteMarker_IsRemoved_WhenTheRuleAsks()
    {
        await _store.SetBucketVersioningAsync(_bucket.Name, BucketVersioning.Enabled, _ct);
        await PutAsync("gone");
        await _store.DeleteObjectVersionsAsync(_bucket.Id, [new ObjectToDelete("gone", null)], _ct);
        var versions = await VersionsAsync();
        await _store.DeleteObjectVersionsAsync(_bucket.Id, [new ObjectToDelete("gone", VersionRef.Of(versions[1].VersionId))], _ct);
        await SetRulesAsync(Rule(markers: true));

        var run = await _runner.RunAsync(_ct);

        Assert.Equal(1, run.DeleteMarkersRemoved);
        Assert.Empty(await VersionsAsync());
    }

    [Fact]
    public async Task IncompleteUploads_AreAbortedAfterTheirDays()
    {
        var upload = new MultipartUpload(Guid.CreateVersion7(), _bucket.Id, "big.bin", StoredObject.DefaultContentType,
            null, null, [], [], Now);
        await _store.TryCreateUploadAsync(upload, _ct);
        await _store.CommitPartAsync(new UploadPart(upload.UploadId, 1,
            new BlobLocator(Guid.CreateVersion7(), "node-1", 5, "d41d8cd98f00b204e9800998ecf8427e"), null, Now), _ct);
        await SetRulesAsync(Rule(abortDays: 2));

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await _runner.RunAsync(_ct)).UploadsAborted);
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await _runner.RunAsync(_ct)).UploadsAborted);
        Assert.Null(await _store.FindUploadAsync(_bucket.Id, "big.bin", upload.UploadId, _ct));
        Assert.Equal(1L, (await _store.GetRetainedBlobsAsync(_ct)).Count);
    }

    [Fact]
    public async Task DisabledRules_DoNothing()
    {
        await PutAsync("a");
        await _store.SetLifecycleAsync(_bucket.Id, [Rule(days: 1) with { Enabled = false }], _ct);

        _clock.Advance(TimeSpan.FromDays(10));
        await _runner.RunAsync(_ct);

        Assert.Equal(["a"], await CurrentKeysAsync());
    }

    /// <summary>Counts release reports; the released blobs themselves are recorded by the store (retained blobs).</summary>
    private sealed class RecordingDeletions : IBlobDeletions
    {
        public int Reports { get; private set; }

        public ValueTask ReleasedAsync()
        {
            Reports++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> CollectAsync() => ValueTask.FromResult(0);
    }
}
