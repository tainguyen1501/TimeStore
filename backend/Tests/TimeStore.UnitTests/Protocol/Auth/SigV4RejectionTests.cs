using TimeStore.Protocol.Auth;
using TimeStore.UnitTests.Fakes;
using static TimeStore.UnitTests.Protocol.Auth.SigV4DocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Auth;

/// <summary>Every way a request can fail authentication, starting from the valid documentation examples.</summary>
public sealed class SigV4RejectionTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private async Task<string> FailCode(FakeSigV4Request request, DateTimeOffset? now = null, string region = "us-east-1")
    {
        var result = await Verifier(now ?? SignedAt, region).VerifyAsync(request, _ct);
        Assert.True(result.IsFailure, "Expected authentication to fail.");
        return result.Error.Code;
    }

    [Fact]
    public async Task TamperedSignature_IsSignatureMismatch() =>
        Assert.Equal(S3AuthErrors.Codes.SignatureMismatch,
            await FailCode(GetObject("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb42")));

    [Fact]
    public async Task ChangedSignedHeaderValue_IsSignatureMismatch() =>
        Assert.Equal(S3AuthErrors.Codes.SignatureMismatch, await FailCode(GetObject().With("range", "bytes=0-99")));

    [Fact]
    public async Task ChangedPath_IsSignatureMismatch() =>
        Assert.Equal(S3AuthErrors.Codes.SignatureMismatch, await FailCode(GetObject().WithTarget("/other.txt")));

    [Fact]
    public async Task MalformedSignatureHex_IsSignatureMismatch() =>
        Assert.Equal(S3AuthErrors.Codes.SignatureMismatch, await FailCode(GetObject("zz")));

    [Fact]
    public async Task UnknownAccessKey_IsInvalidAccessKey()
    {
        var request = GetObject().With("authorization",
            GetObject().Headers["authorization"].Replace(AccessKey, "AKIAUNKNOWN0000000000"));

        Assert.Equal(S3AuthErrors.Codes.InvalidAccessKey, await FailCode(request));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-16)]
    public async Task ClockSkewOver15Minutes_IsTimeSkewed(int minutes) =>
        Assert.Equal(S3AuthErrors.Codes.TimeSkewed, await FailCode(GetObject(), SignedAt.AddMinutes(minutes)));

    [Fact]
    public async Task UnsignedAmzHeader_IsAccessDenied() =>
        Assert.Equal(S3AuthErrors.Codes.AccessDenied, await FailCode(GetObject().With("x-amz-meta-owner", "mallory")));

    [Fact]
    public async Task WrongRegion_IsHeaderMalformed() =>
        Assert.Equal(S3AuthErrors.Codes.HeaderMalformed, await FailCode(GetObject(), region: "eu-west-1"));

    [Fact]
    public async Task NoAuthentication_IsAccessDenied() =>
        Assert.Equal(S3AuthErrors.Codes.AccessDenied, await FailCode(GetObject().Without("authorization")));

    [Fact]
    public async Task SignatureVersion2_IsUnsupported() =>
        Assert.Equal(S3AuthErrors.Codes.UnsupportedMechanism,
            await FailCode(GetObject().With("authorization", "AWS AKIAIOSFODNN7EXAMPLE:frJIUN8DYpKDtOLCwo//yllqDzg=")));

    [Fact]
    public async Task HeaderAndPresigned_IsUnsupported() =>
        Assert.Equal(S3AuthErrors.Codes.UnsupportedMechanism,
            await FailCode(Presigned().With("authorization", GetObject().Headers["authorization"])));

    [Fact]
    public async Task SessionToken_IsInvalidToken() =>
        Assert.Equal(S3AuthErrors.Codes.InvalidToken, await FailCode(GetObject().With("x-amz-security-token", "t")));

    [Fact]
    public async Task MissingContentSha256_IsRejected() =>
        Assert.Equal(S3AuthErrors.Codes.MissingContentSha256, await FailCode(GetObject().Without("x-amz-content-sha256")));

    [Fact]
    public async Task InvalidContentSha256_IsRejected() =>
        Assert.Equal(S3AuthErrors.Codes.InvalidContentSha256,
            await FailCode(GetObject().With("x-amz-content-sha256", "not-a-hash")));

    [Fact]
    public async Task MissingCredentialParts_IsHeaderMalformed() =>
        Assert.Equal(S3AuthErrors.Codes.HeaderMalformed,
            await FailCode(GetObject().With("authorization", "AWS4-HMAC-SHA256 Signature=abc")));

    [Fact]
    public async Task PresignedAfterExpiry_IsAccessDenied() =>
        Assert.Equal(S3AuthErrors.Codes.AccessDenied, await FailCode(Presigned(), SignedAt.AddSeconds(86_401)));

    [Fact]
    public async Task PresignedFromTheFuture_IsAccessDenied() =>
        Assert.Equal(S3AuthErrors.Codes.AccessDenied, await FailCode(Presigned(), SignedAt.AddHours(-1)));

    [Theory]
    [InlineData("X-Amz-Expires=86400", "X-Amz-Expires=604801")]
    [InlineData("X-Amz-Expires=86400", "X-Amz-Expires=soon")]
    [InlineData("&X-Amz-Date=20130524T000000Z", "")]
    public async Task PresignedWithBadParameters_IsQueryMalformed(string find, string replace) =>
        Assert.Equal(S3AuthErrors.Codes.QueryMalformed,
            await FailCode(Presigned().WithTarget(Presigned().RawTarget.Replace(find, replace))));

    [Theory]
    [InlineData("0")]
    [InlineData("-1000")]
    public async Task PresignedWithANonPositiveLifetime_IsDenied_NotMalformed(string expires)
    {
        // A URL valid for no time at all has already expired (Amazon S3: 403 "Request has expired"), whoever signed it.
        var code = await FailCode(Presigned().WithTarget(Presigned().RawTarget.Replace("X-Amz-Expires=86400", $"X-Amz-Expires={expires}")));

        Assert.Contains(code, new[] { S3AuthErrors.Codes.AccessDenied, S3AuthErrors.Codes.SignatureMismatch });
    }

    [Fact]
    public async Task PresignedWithExtraUnsignedParameter_IsSignatureMismatch() =>
        Assert.Equal(S3AuthErrors.Codes.SignatureMismatch,
            await FailCode(Presigned().WithTarget(Presigned().RawTarget + "&response-content-type=text%2Fhtml"), SignedAt.AddHours(1)));
}
