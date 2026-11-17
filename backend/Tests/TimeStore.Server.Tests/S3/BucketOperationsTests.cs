using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>
/// Bucket operations end to end: the AWS SDK for .NET over HTTP against a running server (see
/// <see cref="TimeStoreServer"/>). Every test uses its own bucket names.
/// </summary>
public sealed class BucketOperationsTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IDisposable
{
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _s3.Dispose();

    private static string NewBucket() => $"e2e-{Guid.NewGuid():N}";

    private async Task<AmazonS3Exception> Fails(Func<Task> call) => await Assert.ThrowsAnyAsync<AmazonS3Exception>(call); // the SDK throws typed subclasses (NoSuchBucketException, ...)

    [Fact]
    public async Task CreateBucket_ThenEveryReadOperation_SeesIt()
    {
        var bucket = NewBucket();

        await _s3.PutBucketAsync(bucket, _ct);

        var list = await _s3.ListBucketsAsync(_ct);
        Assert.Contains(list.Buckets, b => b.BucketName == bucket);

        var location = await _s3.GetBucketLocationAsync(bucket, _ct);
        Assert.True(location.Location.Value is "" or TimeStoreServer.Region, location.Location.Value);

        var versioning = await _s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, _ct);
        Assert.Equal(VersionStatus.Off, versioning.VersioningConfig.Status);

        var objects = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "a/", Delimiter = "/" }, _ct);
        Assert.Empty(objects.S3Objects ?? []);
        Assert.False(objects.IsTruncated ?? false);

        var objectsV1 = await _s3.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket }, _ct);
        Assert.Empty(objectsV1.S3Objects ?? []);
    }

    [Fact]
    public async Task HeadBucket_ReportsExistence()
    {
        var bucket = NewBucket();
        await _s3.PutBucketAsync(bucket, _ct);

        using var http = new HttpClient();
        var url = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket, Verb = HttpVerb.HEAD, Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Amazon.S3.Protocol.HTTP,
        });
        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), _ct);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(TimeStoreServer.Region, head.Headers.GetValues("x-amz-bucket-region").Single());
    }

    [Fact]
    public async Task DeleteBucket_RemovesIt()
    {
        var bucket = NewBucket();
        await _s3.PutBucketAsync(bucket, _ct);

        await _s3.DeleteBucketAsync(bucket, _ct);

        Assert.DoesNotContain((await _s3.ListBucketsAsync(_ct)).Buckets ?? [], b => b.BucketName == bucket);
        var error = await Fails(() => _s3.GetBucketLocationAsync(bucket, _ct));
        Assert.Equal("NoSuchBucket", error.ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task CreateBucket_Twice_IsBucketAlreadyOwnedByYou()
    {
        var bucket = NewBucket();
        await _s3.PutBucketAsync(bucket, _ct);

        var error = await Fails(() => _s3.PutBucketAsync(bucket, _ct));

        Assert.Equal("BucketAlreadyOwnedByYou", error.ErrorCode);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public async Task CreateBucket_InvalidName_IsInvalidBucketName() =>
        Assert.Equal("InvalidBucketName", (await Fails(() => _s3.PutBucketAsync("Bad_Bucket_Name", _ct))).ErrorCode);

    [Fact]
    public async Task CreateBucket_InAnotherRegion_IsInvalidLocationConstraint()
    {
        var error = await Fails(() => _s3.PutBucketAsync(
            new PutBucketRequest { BucketName = NewBucket(), BucketRegionName = "eu-west-1" }, _ct));

        Assert.Equal("InvalidLocationConstraint", error.ErrorCode);
    }

    [Fact]
    public async Task MissingBucket_IsNoSuchBucket()
    {
        var bucket = NewBucket();

        Assert.Equal("NoSuchBucket", (await Fails(() => _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, _ct))).ErrorCode);
        Assert.Equal("NoSuchBucket", (await Fails(() => _s3.DeleteBucketAsync(bucket, _ct))).ErrorCode);
    }

    [Theory]
    [InlineData(TimeStoreServer.AccessKey, "wrong-secret-value", "SignatureDoesNotMatch")]
    [InlineData("unknown-key", TimeStoreServer.SecretKey, "InvalidAccessKeyId")]
    public async Task WrongCredentials_AreRejected(string accessKey, string secretKey, string expected)
    {
        using var client = server.Client(accessKey, secretKey);

        var error = await Fails(() => client.ListBucketsAsync(_ct));

        Assert.Equal(expected, error.ErrorCode);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task BucketCreatedOverS3_IsVisibleInTheAdminApi()
    {
        var bucket = NewBucket();
        await _s3.PutBucketAsync(bucket, _ct);

        using var admin = await server.AdminClientAsync();
        var json = await admin.GetStringAsync("/api/v1/buckets", _ct);

        Assert.Contains($"\"name\":\"{bucket}\"", json);
    }

    [Fact]
    public async Task UploadPartCopy_FromAMissingSource_IsNoSuchKey()
    {
        var bucket = NewBucket();
        await _s3.PutBucketAsync(bucket, _ct);

        var upload = await _s3.InitiateMultipartUploadAsync(bucket, "b.txt", _ct);
        var error = await Fails(() => _s3.CopyPartAsync(new CopyPartRequest // UploadPartCopy: step 6b
        {
            SourceBucket = bucket, SourceKey = "a.txt", DestinationBucket = bucket, DestinationKey = "b.txt",
            UploadId = upload.UploadId, PartNumber = 1,
        }, _ct));

        Assert.Equal((HttpStatusCode.NotFound, "NoSuchKey"), (error.StatusCode, error.ErrorCode));
    }
}
