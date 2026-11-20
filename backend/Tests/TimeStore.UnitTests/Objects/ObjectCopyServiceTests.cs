using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Buckets;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;
using TimeStore.Infra.Services.Buckets;
using TimeStore.Infra.Services.Objects;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Objects;

public sealed class ObjectCopyServiceTests : IAsyncLifetime
{
    private const string Bucket = "copies";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-copy", Guid.NewGuid().ToString("N"));
    private readonly InMemoryMetadataStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private ObjectCopyService _service = null!;
    private Guid _bucketId;

    public async ValueTask InitializeAsync()
    {
        var blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await blobs.InitializeAsync(_ct);
        _service = new ObjectCopyService(_store, blobs, Deletions.Immediate(_store, blobs), new ChecksumFactory(), new FixedTimeProvider());
        _bucketId = (await new BucketService(_store, Deletions.Immediate(_store, blobs), new TimeStore.Infra.Services.Policies.BucketPolicyCache(_store), new FixedTimeProvider()).CreateAsync(new CreateBucketRequest(Bucket), _ct)).Value.Id;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private ObjectReader Source(string key, long size, byte[]? content = null) => new(
        new StoredObject(_bucketId, key, Guid.CreateVersion7(), new SingleBlob(new BlobLocator(Guid.CreateVersion7(), "node-1", size, new string('0', 32))),
            new string('0', 32), "text/plain", null, null, null, [], [new ObjectHeader("owner", "felix")], DateTime.UtcNow),
        new MemoryStream(content ?? []));

    private static CopyObjectCommand To(string bucket, string key, bool replace = false) =>
        new(bucket, key, replace, null, [], [], null, OnlyIfAbsent: false);

    [Fact]
    public async Task ASourceAbove5GiB_IsRefused_BeforeAnyByteIsRead()
    {
        var result = await _service.CopyAsync(To(Bucket, "b"), Source("a", ObjectErrors.MaxCopySourceBytes + 1), _ct);

        Assert.Equal(ObjectErrors.Codes.CopySourceTooLarge, result.Error.Code);
    }

    [Fact]
    public async Task AMissingDestinationBucket_IsNoSuchBucket()
    {
        var result = await _service.CopyAsync(To("missing", "b"), Source("a", 3, [1, 2, 3]), _ct);

        Assert.Equal(BucketErrors.Codes.NotFound, result.Error.Code);
    }

    [Fact]
    public async Task OntoItself_NeedsReplace()
    {
        // The source is the key's current version: copying it onto itself unchanged is refused.
        var current = Source("a", 3, [1, 2, 3]);
        await _store.CommitObjectAsync(current.Object, CommitCondition.Always, _ct);
        var refused = await _service.CopyAsync(To(Bucket, "a"), current, _ct);
        var replaced = await _service.CopyAsync(To(Bucket, "a", replace: true), Source("a", 3, [1, 2, 3]), _ct);

        Assert.Equal(ObjectErrors.Codes.CopyToItself, refused.Error.Code);
        Assert.True(replaced.IsSuccess);
        Assert.Empty((await _store.FindObjectAsync(_bucketId, "a", _ct))!.UserMetadata); // REPLACE took the (empty) request metadata
    }

    [Fact]
    public async Task ASourceShorterThanItsRecordedSize_FailsAndStoresNothing()
    {
        await Assert.ThrowsAsync<IOException>(async () => await _service.CopyAsync(To(Bucket, "b"), Source("a", 10, [1, 2, 3]), _ct));

        Assert.Null(await _store.FindObjectAsync(_bucketId, "b", _ct));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories));
    }
}
