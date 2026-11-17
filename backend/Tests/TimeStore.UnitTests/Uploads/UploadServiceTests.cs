using System.Security.Cryptography;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Buckets;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Objects;
using TimeStore.Infra.Services.Buckets;
using TimeStore.Infra.Services.Uploads;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Uploads;

public sealed class UploadServiceTests : IAsyncLifetime
{
    private const string Bucket = "videos";
    private const string Key = "movie.mp4";
    private static readonly byte[] Content = "part content"u8.ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-uploads", Guid.NewGuid().ToString("N"));
    private readonly InMemoryMetadataStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private UploadService _service = null!;

    public async ValueTask InitializeAsync()
    {
        var blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await blobs.InitializeAsync(_ct);
        _service = new UploadService(_store, blobs, Deletions.Immediate(blobs), new ChecksumFactory(), new FixedTimeProvider());
        await new BucketService(_store, Deletions.Immediate(blobs), new TimeStore.Infra.Services.Policies.BucketPolicyCache(_store), new FixedTimeProvider()).CreateAsync(new CreateBucketRequest(Bucket), _ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private int BlobCount() =>
        Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories).Count();

    private async Task<string> CreateAsync(string? algorithm = null, string? type = "COMPOSITE") =>
        (await _service.CreateAsync(
            new CreateUploadCommand(Bucket, Key, "video/mp4", algorithm, algorithm is null ? null : type, [], []), _ct)).Value;

    private ValueTask<TimeStore.Primitives.Result<PartUploaded>> PartAsync(
        string uploadId, int number = 1, byte[]? md5 = null, ObjectChecksum? checksum = null, byte[]? data = null)
    {
        var content = data ?? Content;
        return _service.UploadPartAsync(
            new UploadPartCommand(Bucket, Key, uploadId, number, content.Length, md5), new MemoryStream(content), () => checksum, _ct);
    }

    private static readonly byte[] FullPart = new byte[(int)TimeStore.Domain.Uploads.MultipartUpload.MinPartSize];

    private ValueTask<TimeStore.Primitives.Result<UploadCompleted>> CompleteAsync(
        string uploadId, bool onlyIfAbsent = false, params CompletedPart[] parts) =>
        _service.CompleteAsync(new CompleteUploadCommand(Bucket, Key, uploadId, parts, onlyIfAbsent), _ct);

    // --- CompleteMultipartUpload -----------------------------------------------------------------------------------

    [Fact]
    public async Task Complete_BuildsAManifest_WithTheMd5NETag_AndFreesUnlistedParts()
    {
        var id = await CreateAsync();
        var p1 = (await PartAsync(id, 1, data: FullPart)).Value;
        var p2 = (await PartAsync(id, 2)).Value;
        await PartAsync(id, 3); // never listed

        var completed = await CompleteAsync(id, false, new(1, p1.ETag, null), new(2, $"\"{p2.ETag}\"".Trim('"'), null));

        var md5s = Convert.FromHexString(p1.ETag).Concat(Convert.FromHexString(p2.ETag)).ToArray();
        Assert.Equal($"{Convert.ToHexStringLower(MD5.HashData(md5s))}-2", completed.Value.ETag);
        var blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        var head = await new TimeStore.Infra.Services.Objects.ObjectService(_store, blobs, Deletions.Immediate(blobs), new FixedTimeProvider())
            .HeadAsync(Bucket, Key, _ct);
        var data = Assert.IsType<MultipartBlobs>(head.Value.Data);
        Assert.Equal([0L, FullPart.Length], data.Parts.Select(p => p.Offset));
        Assert.Equal(FullPart.Length + Content.Length, head.Value.Size);
        Assert.Equal("video/mp4", head.Value.ContentType);
        Assert.Equal(2, BlobCount()); // part 3 freed
        Assert.Equal(UploadErrors.Codes.NotFound, (await _service.ListPartsAsync(Bucket, Key, id, 0, 10, _ct)).Error.Code);
    }

    [Fact]
    public async Task Complete_Retried_AfterSuccess_ReturnsTheSameResult()
    {
        var id = await CreateAsync();
        var part = (await PartAsync(id)).Value;

        var first = await CompleteAsync(id, false, new CompletedPart(1, part.ETag, null));
        var retry = await CompleteAsync(id, false, new CompletedPart(1, part.ETag, null));

        Assert.True(retry.IsSuccess);
        Assert.Equal(first.Value.ETag, retry.Value.ETag);
    }

    [Theory]
    [InlineData(new[] { 2, 1 }, UploadErrors.Codes.InvalidPartOrder)]
    [InlineData(new[] { 2, 1, 1 }, UploadErrors.Codes.InvalidPartOrder)]
    [InlineData(new[] { 0 }, UploadErrors.Codes.InvalidPartNumber)]
    [InlineData(new int[0], UploadErrors.Codes.NoParts)]
    [InlineData(new[] { 1, 4 }, UploadErrors.Codes.InvalidPart)] // 4 was never uploaded
    public async Task Complete_RejectsBadPartLists(int[] numbers, string code)
    {
        var id = await CreateAsync();
        var etag = (await PartAsync(id, 1, data: FullPart)).Value.ETag;
        await PartAsync(id, 2);

        var result = await CompleteAsync(id, false, numbers.Select(n => new CompletedPart(n, etag, null)).ToArray());

        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public async Task Complete_WithAWrongETag_IsInvalidPart()
    {
        var id = await CreateAsync();
        await PartAsync(id);

        Assert.Equal(UploadErrors.Codes.InvalidPart, (await CompleteAsync(id, false, new CompletedPart(1, new string('0', 32), null))).Error.Code);
    }

    [Fact]
    public async Task Complete_WithASmallPartBeforeTheLast_IsEntityTooSmall()
    {
        var id = await CreateAsync();
        var small = (await PartAsync(id, 1)).Value;
        var last = (await PartAsync(id, 2, data: FullPart)).Value;

        var result = await CompleteAsync(id, false, new(1, small.ETag, null), new(2, last.ETag, null));

        Assert.Equal(UploadErrors.Codes.PartTooSmall, result.Error.Code);
    }

    [Fact]
    public async Task Complete_WithCrc32_BuildsTheCompositeChecksum()
    {
        var id = await CreateAsync("CRC32");
        byte[] c1 = [1, 2, 3, 4], c2 = [5, 6, 7, 8];
        var p1 = (await PartAsync(id, 1, data: FullPart, checksum: new ObjectChecksum("CRC32", c1))).Value;
        var p2 = (await PartAsync(id, 2, checksum: new ObjectChecksum("CRC32", c2))).Value;

        var completed = await CompleteAsync(id, false,
            new(1, p1.ETag, new ObjectChecksum("CRC32", c1)), new(2, p2.ETag, null));

        using var crc = new ChecksumFactory().Create(ChecksumAlgorithm.Crc32);
        crc.Append([.. c1, .. c2]);
        var expected = new byte[4];
        crc.GetHashAndReset(expected);
        Assert.Equal(expected, completed.Value.Checksum!.Digest);
        Assert.Equal("COMPOSITE", completed.Value.ChecksumType);
    }

    [Fact]
    public async Task Complete_ClaimingAnotherPartChecksum_IsInvalidPart()
    {
        var id = await CreateAsync("CRC32");
        var part = (await PartAsync(id, checksum: new ObjectChecksum("CRC32", [1, 2, 3, 4]))).Value;

        var result = await CompleteAsync(id, false, new CompletedPart(1, part.ETag, new ObjectChecksum("CRC32", [9, 9, 9, 9])));

        Assert.Equal(UploadErrors.Codes.InvalidPart, result.Error.Code);
    }

    [Fact]
    public async Task Complete_FullObject_CombinesThePartCrcs_IntoTheCrcOfTheWholeObject()
    {
        var factory = new ChecksumFactory();
        byte[] Crc(byte[] data)
        {
            using var crc = factory.Create(ChecksumAlgorithm.Crc32C);
            crc.Append(data);
            var digest = new byte[4];
            crc.GetHashAndReset(digest);
            return digest;
        }

        var id = await CreateAsync("CRC32C", "FULL_OBJECT");
        var p1 = (await PartAsync(id, 1, data: FullPart, checksum: new ObjectChecksum("CRC32C", Crc(FullPart)))).Value;
        var p2 = (await PartAsync(id, 2, checksum: new ObjectChecksum("CRC32C", Crc(Content)))).Value;
        var whole = Crc([.. FullPart, .. Content]);

        var wrong = await _service.CompleteAsync(new CompleteUploadCommand(Bucket, Key, id,
            [new(1, p1.ETag, null), new(2, p2.ETag, null)], false, new ObjectChecksum("CRC32C", [0, 0, 0, 0])), _ct);
        var completed = await _service.CompleteAsync(new CompleteUploadCommand(Bucket, Key, id,
            [new(1, p1.ETag, null), new(2, p2.ETag, null)], false, new ObjectChecksum("CRC32C", whole)), _ct);

        Assert.Equal(UploadErrors.Codes.BadObjectChecksum, wrong.Error.Code);
        Assert.Equal(whole, completed.Value.Checksum!.Digest);
        Assert.Equal("FULL_OBJECT", completed.Value.ChecksumType);
    }

    [Fact]
    public async Task Complete_IfNoneMatch_OverAnExistingObject_KeepsTheUpload()
    {
        var first = await CreateAsync();
        await CompleteAsync(first, false, new CompletedPart(1, (await PartAsync(first)).Value.ETag, null));
        var second = await CreateAsync();
        var part = (await PartAsync(second)).Value;

        var result = await CompleteAsync(second, onlyIfAbsent: true, new CompletedPart(1, part.ETag, null));

        Assert.Equal(ObjectErrors.Codes.AlreadyExists, result.Error.Code);
        Assert.True((await _service.ListPartsAsync(Bucket, Key, second, 0, 10, _ct)).IsSuccess);
    }

    [Fact]
    public async Task Complete_OverAnExistingMultipartObject_FreesItsParts()
    {
        var first = await CreateAsync();
        await CompleteAsync(first, false, new CompletedPart(1, (await PartAsync(first)).Value.ETag, null));
        var second = await CreateAsync();

        await CompleteAsync(second, false, new CompletedPart(1, (await PartAsync(second)).Value.ETag, null));

        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task Create_ReturnsA32HexId_AndValidatesLikePutObject()
    {
        var id = await CreateAsync();

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(ObjectErrors.Codes.InvalidKey,
            (await _service.CreateAsync(new CreateUploadCommand(Bucket, "bad\u0001key", null, null, null, [], []), _ct)).Error.Code);
        Assert.Equal(BucketErrors.Codes.NotFound,
            (await _service.CreateAsync(new CreateUploadCommand("missing", Key, null, null, null, [], []), _ct)).Error.Code);
    }

    [Fact]
    public async Task UploadPart_ReturnsTheMd5ETag_AndReplacingAPartFreesTheOldBlob()
    {
        var id = await CreateAsync();

        var first = await PartAsync(id);
        await PartAsync(id); // same number again

        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Content)), first.Value.ETag);
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task UploadPart_ToAnUnknownUpload_WritesNothing()
    {
        await CreateAsync();

        Assert.Equal(UploadErrors.Codes.NotFound, (await PartAsync(Guid.CreateVersion7().ToString("N"))).Error.Code);
        Assert.Equal(UploadErrors.Codes.NotFound, (await PartAsync("not-an-id")).Error.Code);
        Assert.Equal(0, BlobCount());
    }

    [Fact]
    public async Task UploadPart_WithAWrongMd5_LeavesNoBlob()
    {
        var id = await CreateAsync();

        Assert.Equal(ObjectErrors.Codes.BadDigest, (await PartAsync(id, md5: new byte[16])).Error.Code);
        Assert.Equal(0, BlobCount());
    }

    [Fact]
    public async Task UploadPart_MustUseTheUploadsChecksumAlgorithm()
    {
        var id = await CreateAsync("CRC32");

        var missing = await PartAsync(id);
        var other = await PartAsync(id, checksum: new ObjectChecksum("CRC32C", [1, 2, 3, 4]));
        var matching = await PartAsync(id, checksum: new ObjectChecksum("CRC32", [1, 2, 3, 4]));

        Assert.Equal(UploadErrors.Codes.ChecksumMismatch, missing.Error.Code);
        Assert.Equal(UploadErrors.Codes.ChecksumMismatch, other.Error.Code);
        Assert.True(matching.IsSuccess);
        Assert.Equal(1, BlobCount());
        var parts = (await _service.ListPartsAsync(Bucket, Key, id, 0, 10, _ct)).Value;
        Assert.Equal([1, 2, 3, 4], Assert.Single(parts.Page.Parts).Checksum);
        Assert.Equal("CRC32", parts.Upload.ChecksumAlgorithm);
    }

    [Fact]
    public async Task Abort_FreesEveryPart_AndIsNotRepeatable()
    {
        var id = await CreateAsync();
        await PartAsync(id, 1);
        await PartAsync(id, 2);

        Assert.True((await _service.AbortAsync(Bucket, Key, id, _ct)).IsSuccess);

        Assert.Equal(0, BlobCount());
        Assert.Equal(UploadErrors.Codes.NotFound, (await _service.AbortAsync(Bucket, Key, id, _ct)).Error.Code);
        Assert.Equal(UploadErrors.Codes.NotFound, (await _service.ListPartsAsync(Bucket, Key, id, 0, 10, _ct)).Error.Code);
    }

    [Fact]
    public async Task Upload_IsScopedToItsKey()
    {
        var id = await CreateAsync();

        Assert.Equal(UploadErrors.Codes.NotFound, (await _service.AbortAsync(Bucket, "other.mp4", id, _ct)).Error.Code);
    }

    [Fact]
    public async Task ListUploads_RejectsAMalformedUploadIdMarker()
    {
        await CreateAsync();

        var result = await _service.ListUploadsAsync(Bucket, "", null, Key, "zzz", 10, _ct);

        Assert.Equal(UploadErrors.Codes.InvalidMarker, result.Error.Code);
        Assert.Single((await _service.ListUploadsAsync(Bucket, "", null, null, "zzz", 10, _ct)).Value.Uploads); // ignored without key-marker
    }
}
