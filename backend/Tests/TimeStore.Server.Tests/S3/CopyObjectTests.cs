using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>CopyObject end to end with the AWS SDK for .NET (step 4c).</summary>
public sealed class CopyObjectTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"e2e-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _s3.PutBucketAsync(_bucket, _ct);

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<byte[]> ReadAsync(string bucket, string key)
    {
        using var response = await _s3.GetObjectAsync(bucket, key, _ct);
        var copy = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copy, _ct);
        return copy.ToArray();
    }

    private Task PutAsync(string key, string body, string contentType = "text/plain", string? owner = "felix") =>
        _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = key, ContentBody = body, ContentType = contentType,
            Metadata = { ["owner"] = owner },
        }, _ct);

    [Fact]
    public async Task Copy_KeepsBytesETagAndMetadata_ByDefault()
    {
        await PutAsync("source.txt", "copy me");

        var copied = await _s3.CopyObjectAsync(_bucket, "source.txt", _bucket, "target.txt", _ct);

        var source = await _s3.GetObjectMetadataAsync(_bucket, "source.txt", _ct);
        var target = await _s3.GetObjectMetadataAsync(_bucket, "target.txt", _ct);
        Assert.Equal(source.ETag, copied.ETag);
        Assert.Equal(source.ETag, target.ETag);
        Assert.Equal("text/plain", target.Headers.ContentType);
        Assert.Equal("felix", target.Metadata["owner"]);
        Assert.Equal("copy me"u8.ToArray(), await ReadAsync(_bucket, "target.txt"));
    }

    [Fact]
    public async Task Copy_WithReplace_TakesTheRequestsMetadata_AndMayTargetItself()
    {
        await PutAsync("self.txt", "same bytes");

        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "self.txt", DestinationBucket = _bucket, DestinationKey = "self.txt",
            MetadataDirective = S3MetadataDirective.REPLACE, ContentType = "application/json", Metadata = { ["owner"] = "time" },
        }, _ct);

        var head = await _s3.GetObjectMetadataAsync(_bucket, "self.txt", _ct);
        Assert.Equal("application/json", head.Headers.ContentType);
        Assert.Equal("time", head.Metadata["owner"]);
        Assert.Equal("same bytes"u8.ToArray(), await ReadAsync(_bucket, "self.txt"));
    }

    [Fact]
    public async Task Copy_OntoItself_WithoutReplace_IsInvalidRequest()
    {
        await PutAsync("loop.txt", "x");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _s3.CopyObjectAsync(_bucket, "loop.txt", _bucket, "loop.txt", _ct));

        Assert.Equal("InvalidRequest", error.ErrorCode);
    }

    [Fact]
    public async Task Copy_AcrossBuckets_AndOfAMultipartSource()
    {
        var other = $"e2e-{Guid.NewGuid():N}";
        await _s3.PutBucketAsync(other, _ct);
        var parts = new[] { new byte[5 * 1024 * 1024], new byte[777] };
        new Random(4).NextBytes(parts[0]);
        new Random(5).NextBytes(parts[1]);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "big.bin", _ct);
        var etags = new List<PartETag>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = await _s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket, Key = "big.bin", UploadId = upload.UploadId, PartNumber = i + 1, InputStream = new MemoryStream(parts[i]),
            }, _ct);
            etags.Add(new PartETag(i + 1, part.ETag));
        }
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = "big.bin", UploadId = upload.UploadId, PartETags = etags,
        }, _ct);

        var copied = await _s3.CopyObjectAsync(_bucket, "big.bin", other, "copy.bin", _ct);

        Assert.DoesNotContain("-", copied.ETag); // the copy is one blob: its ETag is the MD5 of all bytes
        Assert.Equal(parts.SelectMany(p => p).ToArray(), await ReadAsync(other, "copy.bin"));
        await _s3.DeleteObjectAsync(_bucket, "big.bin", _ct); // the copy shares no blob with its source
        Assert.Equal(parts[0].Length + parts[1].Length, (await ReadAsync(other, "copy.bin")).Length);
    }

    [Fact]
    public async Task Copy_ComputesTheRequestedChecksum()
    {
        await PutAsync("sum.txt", "checksum me");

        var copied = await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "sum.txt", DestinationBucket = _bucket, DestinationKey = "sum-copy.txt",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        }, _ct);

        var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucket, Key = "sum-copy.txt", ChecksumMode = ChecksumMode.ENABLED,
        }, _ct);
        Assert.Equal(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("checksum me"u8)), head.ChecksumSHA256);
        Assert.Equal(head.ChecksumSHA256, copied.ChecksumSHA256);
    }

    [Fact]
    public async Task Copy_SourceConditions_FailWith412()
    {
        await PutAsync("cond.txt", "x");
        var etag = (await _s3.GetObjectMetadataAsync(_bucket, "cond.txt", _ct)).ETag;

        var wrongMatch = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "cond.txt", DestinationBucket = _bucket, DestinationKey = "c1",
            ETagToMatch = "\"00000000000000000000000000000000\"",
        }, _ct));
        var noneMatch = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "cond.txt", DestinationBucket = _bucket, DestinationKey = "c2",
            ETagToNotMatch = etag,
        }, _ct));
        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "cond.txt", DestinationBucket = _bucket, DestinationKey = "c3", ETagToMatch = etag,
        }, _ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, wrongMatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, noneMatch.StatusCode);
    }

    [Fact]
    public async Task Copy_OfAMissingSource_IsNoSuchKey()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _s3.CopyObjectAsync(_bucket, "missing.txt", _bucket, "target.txt", _ct));

        Assert.True(error.ErrorCode == "NoSuchKey", $"{error.ErrorCode} ({error.StatusCode}): {error.Message}");
    }

    [Fact]
    public async Task Copy_OfAKeyThatNeedsEncoding()
    {
        await PutAsync("folder/ảnh có dấu & space.txt", "unicode");

        await _s3.CopyObjectAsync(_bucket, "folder/ảnh có dấu & space.txt", _bucket, "plain.txt", _ct);

        Assert.Equal("unicode"u8.ToArray(), await ReadAsync(_bucket, "plain.txt"));
    }
}
