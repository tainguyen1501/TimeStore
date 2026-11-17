using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Streaming;
using TimeStore.UnitTests.Fakes;
using static TimeStore.UnitTests.Protocol.Streaming.AwsChunkedDocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Streaming;

/// <summary>
/// On CompleteMultipartUpload an <c>x-amz-checksum-*</c> header is the object's checksum (composite values even end in
/// <c>-N</c>), so it must not be verified against the XML body; on every other operation it must.
/// </summary>
public sealed class CompleteBodyChecksumTests
{
    private static readonly byte[] Xml = "<CompleteMultipartUpload/>"u8.ToArray();

    private static readonly SigV4Principal Unsigned =
        Principal("0") with { PayloadMode = PayloadMode.Unsigned, PayloadSha256 = null };

    private static FakeSigV4Request Headers(string value) =>
        new("POST", "/bucket/key?uploadId=1", new Dictionary<string, string> { ["x-amz-checksum-crc32"] = value });

    [Theory]
    [InlineData("AAAAAA==")]
    [InlineData("AAAAAA==-3")]
    public async Task ObjectChecksumHeader_IsLeftForTheCompletion(string value)
    {
        var opened = S3RequestBody.Open(
            new MemoryStream(Xml), Xml.Length, Unsigned, Headers(value), new ChecksumFactory(), headerChecksumsDescribeBody: false);

        var copy = new MemoryStream();
        await opened.Value.Body.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(Xml, copy.ToArray());
        Assert.Null(opened.Value.VerifiedChecksum);
    }

    [Fact]
    public async Task OnOtherOperations_TheHeaderIsVerifiedAgainstTheBody()
    {
        var opened = S3RequestBody.Open(new MemoryStream(Xml), Xml.Length, Unsigned, Headers("AAAAAA=="), new ChecksumFactory());

        await Assert.ThrowsAsync<S3BodyVerificationException>(() =>
            opened.Value.Body.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
    }
}
