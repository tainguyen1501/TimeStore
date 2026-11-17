using TimeStore.Protocol.Auth;
using TimeStore.UnitTests.Fakes;
using static TimeStore.UnitTests.Protocol.Auth.SigV4DocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Auth;

/// <summary>Step 7f: share links are presigned GET URLs that Amazon S3 and our own verifier accept.</summary>
public sealed class SigV4PresignerTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void DocumentationExample_GivesThePublishedSignature()
    {
        var url = SigV4Presigner.Presign(
            new Uri("https://" + Host), "/test.txt", [], AccessKey, SecretKey, "us-east-1", SignedAt.UtcDateTime, 86400);

        Assert.Equal(
            $"https://{Host}/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"
            + "&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host"
            + "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            url);
    }

    [Theory]
    [InlineData("/photos/a b/ü+%.txt", null)]
    [InlineData("/photos/plain.txt", "0192f3a1b2c3d4e5f60718293a4b5c6d")]
    public async Task OddKeysAndVersions_VerifyOnANonDefaultPort(string path, string? versionId)
    {
        var endpoint = new Uri("http://127.0.0.1:9000");
        var url = new Uri(SigV4Presigner.Presign(
            endpoint, path, versionId is null ? [] : [KeyValuePair.Create("versionId", versionId)],
            AccessKey, SecretKey, "us-east-1", SignedAt.UtcDateTime, 3600));

        var request = new FakeSigV4Request("GET", url.PathAndQuery, new() { ["host"] = "127.0.0.1:9000" });
        var verified = await Verifier(SignedAt.AddMinutes(30)).VerifyAsync(request, _ct);
        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Message : null);
        Assert.True(verified.Value.Presigned);

        var expired = await Verifier(SignedAt.AddHours(2)).VerifyAsync(request, _ct);
        Assert.True(expired.IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SigV4Presigner.MaxExpiresSeconds + 1)]
    public void ExpiryOutsideSevenDays_Throws(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SigV4Presigner.Presign(
            new Uri("http://localhost:9000"), "/b/k", [], AccessKey, SecretKey, "us-east-1", SignedAt.UtcDateTime, seconds));
}
