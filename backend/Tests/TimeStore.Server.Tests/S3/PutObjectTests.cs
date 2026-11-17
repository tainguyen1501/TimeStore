using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>PutObject end to end with the AWS SDK for .NET and its default behaviour (aws-chunked, checksum trailers).</summary>
public sealed class PutObjectTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
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

    private async Task<AmazonS3Exception> Fails(Func<Task> call) => await Assert.ThrowsAnyAsync<AmazonS3Exception>(call);

    private PutObjectRequest Request(string key, byte[] content) =>
        new() { BucketName = _bucket, Key = key, InputStream = new MemoryStream(content) };

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(5 * 1024 * 1024)]
    public async Task Put_ReturnsMd5ETag(int size)
    {
        var content = RandomNumberGenerator.GetBytes(size);

        var response = await _s3.PutObjectAsync(Request($"size-{size}.bin", content), _ct);

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        Assert.Equal($"\"{Convert.ToHexStringLower(MD5.HashData(content))}\"", response.ETag);
    }

    public static TheoryData<string> Algorithms() =>
    [
        ChecksumAlgorithm.CRC32.Value, ChecksumAlgorithm.CRC32C.Value, ChecksumAlgorithm.CRC64NVME.Value,
        ChecksumAlgorithm.SHA1.Value, ChecksumAlgorithm.SHA256.Value,
    ];

    [Theory]
    [MemberData(nameof(Algorithms))]
    public async Task Put_WithEveryChecksumAlgorithm_EchoesTheVerifiedChecksum(string algorithm)
    {
        var request = Request("checked.bin", RandomNumberGenerator.GetBytes(200_000));
        request.ChecksumAlgorithm = ChecksumAlgorithm.FindValue(algorithm);

        var response = await _s3.PutObjectAsync(request, _ct);

        var echoed = algorithm switch
        {
            "CRC32" => response.ChecksumCRC32,
            "CRC32C" => response.ChecksumCRC32C,
            "CRC64NVME" => response.ChecksumCRC64NVME,
            "SHA1" => response.ChecksumSHA1,
            _ => response.ChecksumSHA256,
        };
        Assert.False(string.IsNullOrEmpty(echoed), $"{algorithm} checksum was not returned.");
    }

    [Fact]
    public async Task Put_Overwrite_Succeeds()
    {
        await _s3.PutObjectAsync(Request("same.txt", "one"u8.ToArray()), _ct);

        var second = await _s3.PutObjectAsync(Request("same.txt", "two"u8.ToArray()), _ct);

        Assert.Equal($"\"{Convert.ToHexStringLower(MD5.HashData("two"u8))}\"", second.ETag);
    }

    [Fact]
    public async Task Put_IfNoneMatchStar_OnExistingKey_IsPreconditionFailed()
    {
        await _s3.PutObjectAsync(Request("once.txt", "one"u8.ToArray()), _ct);
        var request = Request("once.txt", "two"u8.ToArray());
        request.IfNoneMatch = "*";

        var error = await Fails(() => _s3.PutObjectAsync(request, _ct));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
    }

    [Fact]
    public async Task Put_WrongContentMd5_IsBadDigest()
    {
        var request = Request("md5.txt", "content"u8.ToArray());
        request.MD5Digest = Convert.ToBase64String(MD5.HashData("other"u8));

        Assert.Equal("BadDigest", (await Fails(() => _s3.PutObjectAsync(request, _ct))).ErrorCode);
    }

    [Fact]
    public async Task Put_MetadataOver2KB_IsMetadataTooLarge()
    {
        var request = Request("meta.txt", "content"u8.ToArray());
        request.Metadata.Add("big", new string('x', 2_100));

        Assert.Equal("MetadataTooLarge", (await Fails(() => _s3.PutObjectAsync(request, _ct))).ErrorCode);
    }

    [Fact]
    public async Task Put_KeyWithControlCharacter_IsInvalidArgument() =>
        Assert.Equal("InvalidArgument",
            (await Fails(() => _s3.PutObjectAsync(Request("bad\u0001key", "x"u8.ToArray()), _ct))).ErrorCode);

    [Fact]
    public async Task Put_IntoMissingBucket_IsNoSuchBucket()
    {
        var request = Request("a.txt", "x"u8.ToArray());
        request.BucketName = $"missing-{Guid.NewGuid():N}";

        Assert.Equal("NoSuchBucket", (await Fails(() => _s3.PutObjectAsync(request, _ct))).ErrorCode);
    }

    [Fact]
    public async Task DeleteBucket_WithObjects_IsBucketNotEmpty()
    {
        await _s3.PutObjectAsync(Request("keep.txt", "x"u8.ToArray()), _ct);

        var error = await Fails(() => _s3.DeleteBucketAsync(_bucket, _ct));

        Assert.Equal("BucketNotEmpty", error.ErrorCode);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    // --- Unsupported features (step 4b) ----------------------------------------------------------------------------

    [Fact]
    public async Task Put_AskingForEncryption_IsNotImplemented_AndStoresNothing()
    {
        var bucket = _bucket;

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "secret.txt", ContentBody = "x", ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        }, _ct));

        Assert.Equal("NotImplemented", error.ErrorCode);
        Assert.Empty((await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, _ct)).S3Objects ?? []);
    }

    [Fact]
    public async Task Put_WithAnAcl_IsAccessControlListNotSupported_ExceptBucketOwnerFullControl()
    {
        var bucket = _bucket;

        var shared = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "public.txt", ContentBody = "x", CannedACL = S3CannedACL.PublicRead,
        }, _ct));
        var @private = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "private.txt", ContentBody = "x", CannedACL = S3CannedACL.Private,
        }, _ct));
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "owner.txt", ContentBody = "x", CannedACL = S3CannedACL.BucketOwnerFullControl,
            StorageClass = S3StorageClass.Standard,
        }, _ct);

        Assert.Equal("AccessControlListNotSupported", shared.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, shared.StatusCode);
        Assert.Equal("AccessControlListNotSupported", @private.ErrorCode);
    }

    [Fact]
    public async Task Put_WithTagsOrAnotherStorageClass_IsNotImplemented()
    {
        var bucket = _bucket;

        var tagged = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "t", ContentBody = "x", TagSet = [new Tag { Key = "a", Value = "b" }],
        }, _ct));
        var glacier = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "g", ContentBody = "x", StorageClass = S3StorageClass.Glacier,
        }, _ct));

        Assert.Equal("NotImplemented", tagged.ErrorCode);
        Assert.Equal("NotImplemented", glacier.ErrorCode);
    }

    [Fact]
    public async Task CreateBucket_WithObjectLock_IsNotImplemented()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = $"e2e-{Guid.NewGuid():N}", ObjectLockEnabledForBucket = true,
        }, _ct));

        Assert.Equal("NotImplemented", error.ErrorCode);
    }
}
