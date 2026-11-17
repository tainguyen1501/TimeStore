using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>Step 6b: UploadPartCopy end to end, with the AWS SDK as the client.</summary>
public sealed class UploadPartCopyTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private const int MiB = 1024 * 1024;
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"upc-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _s3.PutBucketAsync(_bucket, _ct);

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<byte[]> PutSourceAsync(string key, int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(data) }, _ct);
        return data;
    }

    private async Task<byte[]> GetAsync(string key)
    {
        using var response = await _s3.GetObjectAsync(_bucket, key, _ct);
        using var copy = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copy, _ct);
        return copy.ToArray();
    }

    private Task<CopyPartResponse> CopyPartAsync(string uploadId, string destination, int partNumber, string sourceKey, long? first = null, long? last = null, string? versionId = null) =>
        _s3.CopyPartAsync(new CopyPartRequest
        {
            SourceBucket = _bucket, SourceKey = sourceKey, SourceVersionId = versionId,
            DestinationBucket = _bucket, DestinationKey = destination, UploadId = uploadId, PartNumber = partNumber,
            FirstByte = first, LastByte = last,
        }, _ct);

    [Fact]
    public async Task CopiedRanges_CompleteIntoTheSourceBytes()
    {
        var source = await PutSourceAsync("big.bin", 6 * MiB + 123);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "copy.bin", _ct);

        var first = await CopyPartAsync(upload.UploadId, "copy.bin", 1, "big.bin", 0, 5 * MiB - 1);
        var second = await CopyPartAsync(upload.UploadId, "copy.bin", 2, "big.bin", 5 * MiB, source.Length - 1);
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = "copy.bin", UploadId = upload.UploadId,
            PartETags = [new PartETag(1, first.ETag), new PartETag(2, second.ETag)],
        }, _ct);

        Assert.Equal(source, await GetAsync("copy.bin"));
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(source.AsSpan(0, 5 * MiB))), first.ETag.Trim('"'));
    }

    [Fact]
    public async Task WithoutARange_TheWholeObjectIsCopied()
    {
        var source = await PutSourceAsync("small.bin", 10);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "whole.bin", _ct);

        var part = await CopyPartAsync(upload.UploadId, "whole.bin", 1, "small.bin");
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = "whole.bin", UploadId = upload.UploadId, PartETags = [new PartETag(1, part.ETag)],
        }, _ct);

        Assert.Equal(source, await GetAsync("whole.bin"));
    }

    [Fact]
    public async Task ARangePastTheEnd_IsInvalidRange()
    {
        await PutSourceAsync("five.bin", 5);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "dest", _ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => CopyPartAsync(upload.UploadId, "dest", 1, "five.bin", 0, 21));

        Assert.Equal((HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange"), (error.StatusCode, error.ErrorCode));
    }

    [Fact]
    public async Task AVersionedSource_IsCopiedAndNamed()
    {
        await _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = _bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, _ct);
        var old = await PutSourceAsync("v.bin", 100);
        var oldVersion = (await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = "v.bin" }, _ct)).Versions[0].VersionId;
        await PutSourceAsync("v.bin", 50);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "from-old.bin", _ct);

        var part = await CopyPartAsync(upload.UploadId, "from-old.bin", 1, "v.bin", versionId: oldVersion);
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = "from-old.bin", UploadId = upload.UploadId, PartETags = [new PartETag(1, part.ETag)],
        }, _ct);

        Assert.Equal(oldVersion, part.CopySourceVersionId);
        Assert.Equal(old, await GetAsync("from-old.bin"));
    }

    [Fact]
    public async Task AnUploadWithAChecksumAlgorithm_GetsTheChecksumOnCopiedParts()
    {
        var source = await PutSourceAsync("crc.bin", 1000);
        var upload = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket, Key = "crc-copy.bin", ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
        }, _ct);

        var part = await CopyPartAsync(upload.UploadId, "crc-copy.bin", 1, "crc.bin");
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = "crc-copy.bin", UploadId = upload.UploadId,
            PartETags = [new PartETag { PartNumber = 1, ETag = part.ETag, ChecksumCRC32 = part.ChecksumCRC32 }],
        }, _ct);

        Assert.False(string.IsNullOrEmpty(part.ChecksumCRC32));
        Assert.Equal(source, await GetAsync("crc-copy.bin"));
    }

    [Fact]
    public async Task AFailedSourceCondition_Is412()
    {
        await PutSourceAsync("cond.bin", 10);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "cond-copy.bin", _ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.CopyPartAsync(new CopyPartRequest
        {
            SourceBucket = _bucket, SourceKey = "cond.bin", DestinationBucket = _bucket, DestinationKey = "cond-copy.bin",
            UploadId = upload.UploadId, PartNumber = 1, ETagToMatch = ["\"00000000000000000000000000000000\""],
        }, _ct));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
    }
}
