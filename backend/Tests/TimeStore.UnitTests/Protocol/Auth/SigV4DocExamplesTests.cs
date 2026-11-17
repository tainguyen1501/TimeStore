using TimeStore.Primitives;
using TimeStore.Protocol.Auth;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Protocol.Auth;

/// <summary>
/// The worked examples of the Amazon S3 documentation ("Examples: Signature Calculations" and the presigned URL
/// example). Credentials, timestamps and expected signatures are the published example values.
/// </summary>
public sealed class SigV4DocExamplesTests
{
    public const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    public const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    public const string Host = "examplebucket.s3.amazonaws.com";
    public const string Timestamp = "20130524T000000Z";
    public static readonly DateTimeOffset SignedAt = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public static SigV4Verifier Verifier(DateTimeOffset now, string region = "us-east-1") =>
        new(new DictionarySecretResolver(new() { [AccessKey] = SecretKey }), new SigningKeyCache(),
            new FixedTimeProvider(now), new SigV4Options(region, TimeSpan.FromMinutes(15)));

    public static FakeSigV4Request GetObject(string signature = "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41") =>
        new("GET", "/test.txt", new()
        {
            ["host"] = Host,
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = Timestamp,
            ["authorization"] = $"AWS4-HMAC-SHA256 Credential={AccessKey}/20130524/us-east-1/s3/aws4_request,"
                + $"SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,Signature={signature}",
        });

    [Fact]
    public async Task GetObject_Example_Verifies()
    {
        var result = await Verifier(SignedAt).VerifyAsync(GetObject(), _ct);

        AssertSuccess(result);
        Assert.Equal(AccessKey, result.Value.AccessKeyId);
        Assert.Equal(PayloadMode.Sha256, result.Value.PayloadMode);
        Assert.False(result.Value.Presigned);
    }

    [Fact]
    public async Task PutObject_Example_Verifies() // key "test$file.text": '$' is %24 in the canonical URI
    {
        var request = new FakeSigV4Request("PUT", "/test$file.text", new()
        {
            ["host"] = Host,
            ["date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["x-amz-date"] = Timestamp,
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
            ["x-amz-content-sha256"] = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072",
            ["authorization"] = $"AWS4-HMAC-SHA256 Credential={AccessKey}/20130524/us-east-1/s3/aws4_request,"
                + "SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class,"
                + "Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd",
        });

        var result = await Verifier(SignedAt).VerifyAsync(request, _ct);

        AssertSuccess(result);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", result.Value.PayloadSha256);
    }

    [Theory]
    [InlineData("/?lifecycle", "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543")]
    [InlineData("/?max-keys=2&prefix=J", "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7")]
    public async Task BucketQueryExamples_Verify(string target, string signature)
    {
        var request = new FakeSigV4Request("GET", target, new()
        {
            ["host"] = Host,
            ["x-amz-date"] = Timestamp,
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["authorization"] = $"AWS4-HMAC-SHA256 Credential={AccessKey}/20130524/us-east-1/s3/aws4_request,"
                + $"SignedHeaders=host;x-amz-content-sha256;x-amz-date,Signature={signature}",
        });

        AssertSuccess(await Verifier(SignedAt).VerifyAsync(request, _ct));
    }

    public static FakeSigV4Request Presigned() =>
        new("GET",
            "/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"
            + "&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host"
            + "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            new() { ["host"] = Host });

    [Fact]
    public async Task PresignedUrl_Example_Verifies()
    {
        var result = await Verifier(SignedAt.AddHours(1)).VerifyAsync(Presigned(), _ct);

        AssertSuccess(result);
        Assert.True(result.Value.Presigned);
        Assert.Equal(PayloadMode.Unsigned, result.Value.PayloadMode);
    }

    private static void AssertSuccess(Result<SigV4Principal> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
}
