using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Primitives;
using TimeStore.Protocol.Auth;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Protocol.Auth;

/// <summary>
/// The AWS SDK for .NET as the reference: the real client signs requests (with its current defaults, including
/// checksum trailers), <see cref="AwsSdkCapture"/> captures them in memory, and our verifier must accept every one.
/// </summary>
public sealed class SigV4AwsSdkTests : IDisposable
{
    private const string Bucket = "photos";

    private readonly AwsSdkCapture _sdk = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public static SigV4Verifier Verifier() => new(
        new DictionarySecretResolver(new() { [AwsSdkCapture.AccessKey] = AwsSdkCapture.SecretKey }),
        new SigningKeyCache(), TimeProvider.System, new SigV4Options("us-east-1", TimeSpan.FromMinutes(15)));

    public void Dispose() => _sdk.Dispose();

    [Fact]
    public async Task ListBuckets_IsAccepted() =>
        await AssertAccepted(() => _sdk.Client.ListBucketsAsync(_ct));

    [Fact]
    public async Task PutBucket_IsAccepted() =>
        await AssertAccepted(() => _sdk.Client.PutBucketAsync(Bucket, _ct));

    [Theory]
    [InlineData("cat.jpg")]
    [InlineData("dir with space/ảnh (1)+x.txt")]
    [InlineData("a/../b//c/")]
    [InlineData("special !$&'()*,;=:@~.txt")]
    public async Task GetObject_WithAnyKey_IsAccepted(string key) =>
        await AssertAccepted(() => _sdk.Client.GetObjectAsync(Bucket, key, _ct));

    [Fact]
    public async Task ListObjectsV2_WithQueryParameters_IsAccepted() =>
        await AssertAccepted(() => _sdk.Client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = Bucket, Prefix = "dir with space/", Delimiter = "/", MaxKeys = 5 }, _ct));

    [Fact]
    public async Task PutObject_WithSdkDefaultChecksums_IsAccepted()
    {
        var principal = await AssertAccepted(() => _sdk.Client.PutObjectAsync(
            new PutObjectRequest { BucketName = Bucket, Key = "hello.txt", ContentBody = "Hello, TimeStore!" }, _ct));

        // Pins what the current SDK sends, so the body verification (step 1c) is tested against reality.
        var request = _sdk.Requests[0].Request;
        Assert.Equal(PayloadMode.StreamingSignedTrailer, principal.PayloadMode);
        Assert.Equal("aws-chunked", request.GetHeader("content-encoding"));
        Assert.Equal("x-amz-checksum-crc32", request.GetHeader("x-amz-trailer"));
    }

    [Fact]
    public async Task PresignedGetUrl_IsAccepted()
    {
        var url = new Uri(await _sdk.Client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = "dir with space/cat.jpg",
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Protocol = Amazon.S3.Protocol.HTTP,
        }));

        var result = await Verifier().VerifyAsync(
            new FakeSigV4Request("GET", url.PathAndQuery, new() { ["host"] = url.Authority }), _ct);

        AssertSuccess(result);
        Assert.True(result.Value.Presigned);
    }

    private async Task<SigV4Principal> AssertAccepted(Func<Task> call)
    {
        var captured = await _sdk.CaptureAsync(call);
        var result = await Verifier().VerifyAsync(captured.Request, _ct);
        AssertSuccess(result);
        return result.Value;
    }

    private static void AssertSuccess(Result<SigV4Principal> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
}
