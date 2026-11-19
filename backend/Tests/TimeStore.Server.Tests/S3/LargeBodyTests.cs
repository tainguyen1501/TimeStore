using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>
/// Uploads over Kestrel's default body limit (30,000,000 bytes) go through: S3 allows 5 GiB per PutObject and per part,
/// and clients such as rclone send files up to 200 MiB in one PutObject.
/// </summary>
public sealed class LargeBodyTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private const int OverKestrelDefault = 40 * 1024 * 1024;

    private static readonly byte[] Content = RandomNumberGenerator.GetBytes(OverKestrelDefault);
    private static readonly string Md5ETag = $"\"{Convert.ToHexStringLower(MD5.HashData(Content))}\"";

    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"e2e-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _s3.PutBucketAsync(_bucket, _ct);

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <param name="chunked">
    /// aws-chunked (the SDK's default) or a plain body with its SHA-256 signed (rclone, curl). UNSIGNED-PAYLOAD takes
    /// the plain body's path too; the SDK sends it over HTTPS only.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PutObject_OverThirtyMegabytes_IsStored(bool chunked)
    {
        var key = $"large-{chunked}.bin";

        var put = await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = new MemoryStream(Content),
            UseChunkEncoding = chunked,
        }, _ct);

        Assert.Equal(HttpStatusCode.OK, put.HttpStatusCode);
        Assert.Equal(Md5ETag, put.ETag);
        await AssertStoredAsync(key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadPart_OverThirtyMegabytes_IsStored(bool chunked)
    {
        var key = $"multipart-{chunked}.bin";
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, key, _ct);

        var part = await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _bucket,
            Key = key,
            UploadId = upload.UploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(Content),
            UseChunkEncoding = chunked,
        }, _ct);
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            UploadId = upload.UploadId,
            PartETags = [new PartETag(1, part.ETag)],
        }, _ct);

        Assert.Equal(Md5ETag, part.ETag);
        await AssertStoredAsync(key);
    }

    private async Task AssertStoredAsync(string key)
    {
        using var get = await _s3.GetObjectAsync(_bucket, key, _ct);
        using var read = new MemoryStream();
        await get.ResponseStream.CopyToAsync(read, _ct);
        Assert.Equal(OverKestrelDefault, read.Length);
        Assert.True(Content.AsSpan().SequenceEqual(read.GetBuffer().AsSpan(0, (int)read.Length)), "the stored bytes differ");
    }
}
