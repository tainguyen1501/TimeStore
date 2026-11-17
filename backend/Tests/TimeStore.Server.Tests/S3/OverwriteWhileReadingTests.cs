using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>A server with the production deletion grace (60 s): released blobs stay readable for a while.</summary>
public sealed class ProductionDeletionServer : TimeStoreServer
{
    protected override int BlobDeleteDelaySeconds => 60;
}

/// <summary>
/// Step 5c: a GET that is reading a multipart object part by part completes even when the object is overwritten or
/// deleted meanwhile (before, the parts it had not reached yet were deleted at once and the read broke).
/// </summary>
public sealed class OverwriteWhileReadingTests(ProductionDeletionServer server) : IClassFixture<ProductionDeletionServer>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadInProgress_Completes_WhenTheObjectIsReplacedOrDeleted(bool delete)
    {
        using var s3 = server.Client();
        var bucket = $"e2e-{Guid.NewGuid():N}";
        await s3.PutBucketAsync(bucket, _ct);
        byte[][] parts = [new byte[5 * 1024 * 1024], new byte[5 * 1024 * 1024], new byte[4321]];
        var random = new Random(delete ? 2 : 1);
        foreach (var part in parts)
            random.NextBytes(part);
        var upload = await s3.InitiateMultipartUploadAsync(bucket, "video.bin", _ct);
        var etags = new List<PartETag>();
        for (var i = 0; i < parts.Length; i++)
        {
            var uploaded = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = "video.bin", UploadId = upload.UploadId, PartNumber = i + 1, InputStream = new MemoryStream(parts[i]),
            }, _ct);
            etags.Add(new PartETag(i + 1, uploaded.ETag));
        }

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = "video.bin", UploadId = upload.UploadId, PartETags = etags,
        }, _ct);

        using var reading = await s3.GetObjectAsync(bucket, "video.bin", _ct);
        var received = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (received.Length < 1024 * 1024) // well inside part 1: parts 2 and 3 are not open yet
            received.Write(buffer, 0, await reading.ResponseStream.ReadAsync(buffer, _ct));

        if (delete)
            await s3.DeleteObjectAsync(bucket, "video.bin", _ct);
        else
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "video.bin", ContentBody = "replaced" }, _ct);
        await reading.ResponseStream.CopyToAsync(received, _ct);

        Assert.Equal(parts.SelectMany(p => p).ToArray(), received.ToArray());
    }
}
