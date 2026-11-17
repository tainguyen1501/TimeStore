using System.Security.Cryptography;
using System.Text;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Infra.Services.Buckets;
using TimeStore.Infra.Services.Objects;
using TimeStore.Protocol.Streaming;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Objects;

/// <summary>
/// PutObject with a real file-system blob store: besides the result, every test checks that no blob is left behind
/// unless it is referenced by the committed object.
/// </summary>
public sealed class ObjectServiceTests : IAsyncLifetime
{
    private const string Bucket = "photos";
    private static readonly byte[] Content = "Hello, TimeStore!"u8.ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-objects", Guid.NewGuid().ToString("N"));
    private readonly InMemoryMetadataStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileSystemBlobStore _blobs = null!;
    private ObjectService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await _blobs.InitializeAsync(_ct);
        _service = new ObjectService(_store, _blobs, Deletions.Immediate(_blobs), new FixedTimeProvider());
        await new BucketService(_store, Deletions.Immediate(_blobs), new TimeStore.Infra.Services.Policies.BucketPolicyCache(_store), new FixedTimeProvider()).CreateAsync(new CreateBucketRequest(Bucket), _ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static PutObjectCommand Command(string key = "a.txt", long? length = null, byte[]? md5 = null,
        IReadOnlyList<ObjectHeader>? metadata = null, bool onlyIfAbsent = false) =>
        new(Bucket, key, length ?? Content.Length, "text/plain", md5, [], metadata ?? [], onlyIfAbsent);

    private ValueTask<TimeStore.Primitives.Result<PutObjectResult>> Put(PutObjectCommand command, Stream? body = null) =>
        _service.PutAsync(command, body ?? new MemoryStream(Content), () => null, _ct);

    private int BlobCount() =>
        Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories).Count();

    [Fact]
    public async Task Put_StoresBlobAndMetadata_ETagIsMd5()
    {
        var result = await Put(Command());

        Assert.True(result.IsSuccess);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Content)), result.Value.ETag);
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task Put_Overwrite_DeletesTheReplacedBlob()
    {
        await Put(Command());
        await Put(Command(length: 6), new MemoryStream("second"u8.ToArray()));

        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task Put_MatchingContentMd5_Succeeds() =>
        Assert.True((await Put(Command(md5: MD5.HashData(Content)))).IsSuccess);

    [Fact]
    public async Task Put_WrongContentMd5_IsBadDigest_AndLeavesNoBlob()
    {
        var result = await Put(Command(md5: new byte[16]));

        Assert.Equal(ObjectErrors.Codes.BadDigest, result.Error.Code);
        Assert.Equal(0, BlobCount());
    }

    [Fact]
    public async Task Put_OnlyIfAbsent_OnExistingKey_IsAlreadyExists_AndLeavesNoExtraBlob()
    {
        await Put(Command());

        var result = await Put(Command(onlyIfAbsent: true));

        Assert.Equal(ObjectErrors.Codes.AlreadyExists, result.Error.Code);
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task Put_ToMissingBucket_IsNoSuchBucket() =>
        Assert.Equal(BucketErrors.Codes.NotFound,
            (await _service.PutAsync(Command() with { Bucket = "missing" }, new MemoryStream(Content), () => null, _ct)).Error.Code);

    [Theory]
    [InlineData("bad\u0001key")]
    [InlineData("bad\u001Fkey")]
    [InlineData("bad￿key")]
    public async Task Put_KeyThatCannotBeListed_IsInvalidKey(string key) =>
        Assert.Equal(ObjectErrors.Codes.InvalidKey, (await Put(Command(key))).Error.Code);

    [Fact]
    public async Task Put_WithoutLength_IsLengthRequired() =>
        Assert.Equal(ObjectErrors.Codes.LengthRequired, (await Put(Command() with { Length = null })).Error.Code);

    [Fact]
    public async Task Put_OverFiveGiB_IsTooLarge() =>
        Assert.Equal(ObjectErrors.Codes.TooLarge, (await Put(Command(length: StoredObject.SinglePutMaxBytes + 1))).Error.Code);

    [Fact]
    public async Task Put_MetadataOver2KB_IsMetadataTooLarge() =>
        Assert.Equal(ObjectErrors.Codes.MetadataTooLarge,
            (await Put(Command(metadata: [new ObjectHeader("big", new string('x', 2_100))]))).Error.Code);

    [Fact]
    public async Task Put_BodyFailingVerification_PropagatesAndLeavesNoBlob()
    {
        var failing = new PayloadVerifyingStream(new MemoryStream(Content), new string('0', 64), null, null, Content.Length);

        await Assert.ThrowsAsync<S3BodyVerificationException>(async () => await Put(Command(), failing));

        Assert.Equal(0, BlobCount());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.TempDirectory)));
    }

    [Fact]
    public async Task Delete_RemovesMetadataAndBlob_AndIsIdempotent()
    {
        await Put(Command());

        Assert.True((await _service.DeleteAsync(Bucket, "a.txt", null, _ct)).IsSuccess);
        Assert.True((await _service.DeleteAsync(Bucket, "a.txt", null, _ct)).IsSuccess); // missing key: still success

        Assert.Equal(ObjectErrors.Codes.NotFound, (await _service.HeadAsync(Bucket, "a.txt", _ct)).Error.Code);
        Assert.Equal(0, BlobCount());
    }

    [Fact]
    public async Task DeleteMany_RemovesEveryBlob()
    {
        await Put(Command("a"));
        await Put(Command("b"));
        await Put(Command("keep"));

        Assert.True((await _service.DeleteManyAsync(Bucket, [new("a", null), new("b", null), new("missing", null)], _ct)).IsSuccess);

        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task DeleteMany_Over1000Keys_IsTooManyKeys() =>
        Assert.Equal(ObjectErrors.Codes.TooManyKeys,
            (await _service.DeleteManyAsync(Bucket, Enumerable.Range(0, 1_001).Select(i => new DeleteObjectEntry($"k{i}", null)).ToArray(), _ct)).Error.Code);

    [Fact]
    public async Task Delete_InMissingBucket_IsNoSuchBucket() =>
        Assert.Equal(BucketErrors.Codes.NotFound, (await _service.DeleteAsync("missing", "a", null, _ct)).Error.Code);

    [Fact]
    public async Task Put_StoresTheVerifiedChecksum()
    {
        var checksum = new ObjectChecksum("CRC32", [1, 2, 3, 4]);

        var result = await _service.PutAsync(Command(), new MemoryStream(Content), () => checksum, _ct);

        Assert.Equal(checksum, result.Value.Checksum);
        var stored = await _store.FindObjectAsync((await _store.FindBucketAsync(Bucket, _ct))!.Id, "a.txt", _ct);
        Assert.Equal("CRC32", stored!.ChecksumAlgorithm);
        Assert.Equal(Encoding.ASCII.GetBytes("\u0001\u0002\u0003\u0004"), stored.Checksum);
    }
}
