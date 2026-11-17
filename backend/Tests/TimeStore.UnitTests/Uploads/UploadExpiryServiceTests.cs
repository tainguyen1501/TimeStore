using TimeStore.Application.Features.Uploads;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Buckets;
using TimeStore.Cryptography.Checksums;
using TimeStore.Infra.Services.Buckets;
using TimeStore.Infra.Services.Uploads;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Uploads;

public sealed class UploadExpiryServiceTests : IAsyncLifetime
{
    private const string Bucket = "uploads";
    private static readonly DateTimeOffset Now = FixedTimeProvider.Default;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-expiry", Guid.NewGuid().ToString("N"));
    private readonly InMemoryMetadataStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileSystemBlobStore _blobs = null!;

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await _blobs.InitializeAsync(_ct);
        await new BucketService(_store, Deletions.Immediate(_blobs), new TimeStore.Infra.Services.Policies.BucketPolicyCache(_store), new FixedTimeProvider()).CreateAsync(new CreateBucketRequest(Bucket), _ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private int BlobCount() =>
        Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories).Count();

    /// <summary>An upload started <paramref name="daysAgo"/> days before <see cref="Now"/>, with one part.</summary>
    private async Task<string> StartUploadAsync(string key, int daysAgo)
    {
        var service = new UploadService(_store, _blobs, Deletions.Immediate(_blobs), new ChecksumFactory(), new FixedTimeProvider(Now.AddDays(-daysAgo)));
        var id = (await service.CreateAsync(new CreateUploadCommand(Bucket, key, null, null, null, [], []), _ct)).Value;
        await service.UploadPartAsync(new UploadPartCommand(Bucket, key, id, 1, 3, null), new MemoryStream([1, 2, 3]), () => null, _ct);
        return id;
    }

    private UploadExpiryService Expiry(TimeSpan? abandonAfter) =>
        new(_store, Deletions.Immediate(_blobs), new UploadExpiryOptions(abandonAfter), new FixedTimeProvider(Now));

    [Fact]
    public async Task AbortsOnlyUploadsOlderThanTheAge_AndFreesTheirParts()
    {
        await StartUploadAsync("old-1", daysAgo: 10);
        await StartUploadAsync("old-2", daysAgo: 8);
        var recent = await StartUploadAsync("recent", daysAgo: 2);

        var aborted = await Expiry(TimeSpan.FromDays(7)).AbortAbandonedAsync(_ct);

        Assert.Equal(2, aborted);
        Assert.Equal(1, BlobCount());
        var left = (await _store.ListUploadsAsync((await _store.FindBucketAsync(Bucket, _ct))!.Id, "", null, null, null, 10, _ct)).Uploads;
        Assert.Equal(recent, UploadIds.ToText(Assert.Single(left).UploadId));
    }

    [Fact]
    public async Task Disabled_AbortsNothing()
    {
        await StartUploadAsync("ancient", daysAgo: 400);

        Assert.Equal(0, await Expiry(abandonAfter: null).AbortAbandonedAsync(_ct));
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task WorksThroughMoreThanOneBatch()
    {
        for (var i = 0; i < 230; i++)
            await StartUploadAsync($"k{i}", daysAgo: 30);

        Assert.Equal(230, await Expiry(TimeSpan.FromDays(7)).AbortAbandonedAsync(_ct));
        Assert.Equal(0, BlobCount());
    }
}
