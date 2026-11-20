using TimeStore.Protocol.Auth;
using TimeStore.UnitTests.Fakes;
using static TimeStore.UnitTests.Protocol.Auth.SigV4DocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Auth;

/// <summary>Replication signs its requests to another S3 endpoint: what it sends must verify like an AWS SDK request.</summary>
public sealed class SigV4RequestSignerTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("a b/ü+%.txt", "")]
    [InlineData("../../x//y", "")]
    [InlineData("video.mp4", "partNumber=2&uploadId=abc%2Bdef")]
    public async Task SignedRequests_Verify_WithOddKeysQueriesAndMetadata(string key, string query)
    {
        var path = SigV4RequestSigner.EncodePath("photos", key);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1:9000",
            ["Content-Type"] = "image/jpeg",
            ["Content-MD5"] = "1B2M2Y8AsgTpgAmY7PhCfg==",
            ["x-amz-meta-owner"] = "  Zoë   Müller ",
        };

        var authorization = SigV4RequestSigner.Sign(
            "PUT", path, query, headers, SigV4Names.UnsignedPayload, AccessKey, SecretKey, "us-east-1", SignedAt.UtcDateTime);

        var sent = headers.ToDictionary(static h => h.Key.ToLowerInvariant(), static h => h.Value);
        sent["authorization"] = authorization;
        var target = path + (query.Length > 0 ? "?" + query : string.Empty);
        var verified = await Verifier(SignedAt.AddMinutes(1)).VerifyAsync(new FakeSigV4Request("PUT", target, sent), _ct);
        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Message : null);
        Assert.Equal(AccessKey, verified.Value.AccessKeyId);
        Assert.Contains("x-amz-meta-owner", authorization); // every x-amz-* header is signed
    }

    [Fact]
    public void Query_IsEncodedAndSorted()
    {
        Assert.Equal("partNumber=10&uploadId=a%2Bb%2Fc", SigV4RequestSigner.Query(("uploadId", "a+b/c"), ("partNumber", "10")));
        Assert.Equal("uploads=", SigV4RequestSigner.Query(("uploads", "")));
        Assert.Equal("/my-bucket/a%20b/c%25", SigV4RequestSigner.EncodePath("my-bucket", "a b/c%"));
    }
}
