using TimeStore.Protocol.Routing;

namespace TimeStore.UnitTests.Protocol;

public sealed class ContinuationTokenTests
{
    [Theory]
    [InlineData("logs/")]
    [InlineData("ảnh 1+2 & <x>.jpg")]
    [InlineData("😀/\u0009tab")]
    public void Token_RoundTrips_AndIsUrlSafe(string lastEntry)
    {
        var token = S3ContinuationToken.Encode(lastEntry);

        Assert.True(S3ContinuationToken.TryDecode(token, out var decoded));
        Assert.Equal(lastEntry, decoded);
        Assert.DoesNotContain(token, c => c is '+' or '/' or '=');
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("_-8")] // valid base64url, but 0xFF... is not UTF-8
    public void Token_NotProducedByThisServer_IsRejected(string token) =>
        Assert.False(S3ContinuationToken.TryDecode(token, out _));
}
