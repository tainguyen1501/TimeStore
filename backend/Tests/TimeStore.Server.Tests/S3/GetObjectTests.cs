using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>GetObject / HeadObject end to end: round trips, headers, ranges, conditions, presigned overrides.</summary>
public sealed class GetObjectTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private static readonly byte[] Content = RandomNumberGenerator.GetBytes(300_000);

    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"e2e-{Guid.NewGuid():N}";
    private string _etag = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _s3.PutBucketAsync(_bucket, _ct);
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = "dir/ảnh 1.bin",
            InputStream = new MemoryStream(Content),
            ContentType = "application/x-test",
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32C,
        };
        request.Metadata.Add("owner", "felix");
        request.Headers.CacheControl = "max-age=60";
        request.Headers.ContentDisposition = "attachment; filename=\"a.bin\"";
        _etag = (await _s3.PutObjectAsync(request, _ct)).ETag;
    }

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private GetObjectRequest Get() => new() { BucketName = _bucket, Key = "dir/ảnh 1.bin" };

    private static async Task<byte[]> ReadAll(GetObjectResponse response)
    {
        using var copy = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private async Task<AmazonS3Exception> Fails(Func<Task> call) => await Assert.ThrowsAnyAsync<AmazonS3Exception>(call);

    [Fact]
    public async Task Get_ReturnsTheExactBytesAndStoredHeaders()
    {
        using var response = await _s3.GetObjectAsync(Get(), _ct);

        Assert.Equal(Content, await ReadAll(response));
        Assert.Equal(_etag, response.ETag);
        Assert.Equal("application/x-test", response.Headers.ContentType);
        Assert.Equal(Content.Length, response.ContentLength);
        Assert.Equal("max-age=60", response.Headers.CacheControl);
        Assert.Equal("attachment; filename=\"a.bin\"", response.Headers.ContentDisposition);
        Assert.Equal("felix", response.Metadata["owner"]);
    }

    [Fact]
    public async Task Get_WithChecksumMode_ReturnsTheStoredChecksum()
    {
        var request = Get();
        request.ChecksumMode = ChecksumMode.ENABLED;

        using var response = await _s3.GetObjectAsync(request, _ct);

        Assert.False(string.IsNullOrEmpty(response.ChecksumCRC32C));
        Assert.Equal(Content, await ReadAll(response)); // the SDK also validates the checksum while reading
    }

    [Fact]
    public async Task Head_ReturnsHeadersWithoutBody()
    {
        var head = await _s3.GetObjectMetadataAsync(_bucket, "dir/ảnh 1.bin", _ct);

        Assert.Equal(_etag, head.ETag);
        Assert.Equal(Content.Length, head.ContentLength);
        Assert.Equal("felix", head.Metadata["owner"]);
    }

    [Theory]
    [InlineData(0, 99)]
    [InlineData(250_000, 299_999)]
    [InlineData(299_999, 299_999)]
    public async Task Get_Range_ReturnsTheSlice(long start, long end)
    {
        var request = Get();
        request.ByteRange = new ByteRange(start, end);

        using var response = await _s3.GetObjectAsync(request, _ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.HttpStatusCode);
        Assert.Equal(Content[(int)start..((int)end + 1)], await ReadAll(response));
        Assert.Equal($"bytes {start}-{end}/{Content.Length}", response.ContentRange);
    }

    [Fact]
    public async Task Get_RangeOutsideTheObject_IsInvalidRange()
    {
        var request = Get();
        request.ByteRange = new ByteRange(400_000, 500_000);

        var error = await Fails(() => _s3.GetObjectAsync(request, _ct));

        Assert.Equal("InvalidRange", error.ErrorCode);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
    }

    [Fact]
    public async Task Get_IfNoneMatchCurrentETag_IsNotModified()
    {
        var request = Get();
        request.EtagToNotMatch = _etag;

        var error = await Fails(() => _s3.GetObjectAsync(request, _ct));

        Assert.Equal(HttpStatusCode.NotModified, error.StatusCode);
    }

    [Fact]
    public async Task Get_IfMatchOtherETag_IsPreconditionFailed()
    {
        var request = Get();
        request.EtagToMatch = "\"0000\"";

        var error = await Fails(() => _s3.GetObjectAsync(request, _ct));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
    }

    [Fact]
    public async Task Get_IfMatchCurrentETag_Proceeds()
    {
        var request = Get();
        request.EtagToMatch = _etag;

        using var response = await _s3.GetObjectAsync(request, _ct);

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
    }

    [Fact]
    public async Task PresignedGet_AppliesResponseOverrides()
    {
        var url = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = "dir/ảnh 1.bin",
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Protocol = Amazon.S3.Protocol.HTTP,
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentType = "text/plain", ContentDisposition = "inline" },
        });

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(Content, await response.Content.ReadAsByteArrayAsync(_ct));
    }

    [Fact]
    public async Task Get_MissingKey_IsNoSuchKey()
    {
        var error = await Fails(() => _s3.GetObjectAsync(_bucket, "missing.txt", _ct));

        Assert.Equal("NoSuchKey", error.ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Get_AfterOverwrite_ReturnsTheNewContent()
    {
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "swap.txt", ContentBody = "one" }, _ct);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "swap.txt", ContentBody = "two" }, _ct);

        using var response = await _s3.GetObjectAsync(_bucket, "swap.txt", _ct);

        Assert.Equal("two"u8.ToArray(), await ReadAll(response));
    }

    [Fact]
    public async Task Get_EmptyObject_ReturnsNoBytes()
    {
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "empty", InputStream = new MemoryStream() }, _ct);

        using var response = await _s3.GetObjectAsync(_bucket, "empty", _ct);

        Assert.Empty(await ReadAll(response));
    }
}
