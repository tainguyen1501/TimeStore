using System.Text;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Streaming;
using static TimeStore.UnitTests.Protocol.Auth.SigV4DocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Streaming;

/// <summary>
/// The chunked-upload examples of the Amazon S3 documentation: 65 KiB of 'a' in chunks of 64 KiB, 1 KiB and 0 bytes,
/// signed (seed 4f232c43…) and signed with a CRC32C trailer (seed 106e2a8a…). The published Content-Length values
/// (66824 and 66946) prove the framing byte for byte.
/// </summary>
public sealed class AwsChunkedDocExamplesTests
{
    public const int PayloadLength = 66_560;
    private const string Scope = "20130524/us-east-1/s3/aws4_request";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public static byte[] SignedBody() => Body(
        "ad80c730a21e5b8d04586a2213dd63b9a0e99e0e2307b0ade35a65485a288648",
        "0055627c9e194cb4542bae2aa5492e3c1575bbb81b612b7d234b86a503ef5497",
        "b6c6ea8a5354eaf15b3cb7646744f4275b71ea724fed81ceb9323e279d449df9",
        "\r\n");

    public static byte[] SignedTrailerBody(string checksum = "sOO8/Q==") => Body(
        "b474d8862b1487a5145d686f57f013e54db672cee1c953b3010fb58501ef5aa2",
        "1c1344b170168f8e65b41376b44b20fe354e373826ccbbe2c1d40a8cae51e5c7",
        "2ca2aba2005185cf7159c6277faf83795951dd77a3a99e6e65d5c9f85863f992",
        $"x-amz-checksum-crc32c:{checksum}\r\n"
        + "x-amz-trailer-signature:d81f82fc3505edab99d459891051a732e8730629a2e4a59689829ca17fe2e435\r\n\r\n");

    public static SigV4Principal Principal(string seed) => new(
        AccessKey, SignedAt.UtcDateTime, Timestamp, Scope, seed,
        SigV4Signer.DeriveSigningKey(SecretKey, "20130524", "us-east-1"), PayloadMode.StreamingSigned, null, false);

    public static AwsChunkedStream SignedStream(byte[] body, long decodedLength = PayloadLength) =>
        new(new MemoryStream(body),
            new AwsChunkedSettings(PayloadMode.StreamingSigned, decodedLength, null,
                Principal("4f232c4386841ef735655705268965c44a0e4690baa4adea153f7db9fa80a0a9")),
            new ChecksumFactory());

    public static AwsChunkedStream SignedTrailerStream(byte[] body) =>
        new(new MemoryStream(body),
            new AwsChunkedSettings(PayloadMode.StreamingSignedTrailer, PayloadLength, ChecksumAlgorithm.Crc32C,
                Principal("106e2a8a18243abcf37539882f36619c00e2dfc72633413f02d3b74544bfeb8e")),
            new ChecksumFactory());

    [Fact]
    public void Framing_MatchesPublishedContentLengths()
    {
        Assert.Equal(66_824, SignedBody().Length);
        Assert.Equal(66_946, SignedTrailerBody().Length);
    }

    [Fact]
    public async Task SignedExample_YieldsPayload()
    {
        await using var stream = SignedStream(SignedBody());

        var payload = await ReadAllAsync(stream);

        Assert.Equal(PayloadLength, payload.Length);
        Assert.All(payload, b => Assert.Equal((byte)'a', b));
    }

    [Fact]
    public async Task SignedTrailerExample_YieldsPayload_AndVerifiesCrc32CAndTrailerSignature()
    {
        await using var stream = SignedTrailerStream(SignedTrailerBody());

        Assert.Equal(PayloadLength, (await ReadAllAsync(stream)).Length);
    }

    [Fact]
    public async Task SmallReadBuffers_GiveTheSameResult()
    {
        await using var stream = SignedTrailerStream(SignedTrailerBody());
        var total = 0;
        var buffer = new byte[7];
        int read;
        while ((read = await stream.ReadAsync(buffer, _ct)) > 0)
            total += read;

        Assert.Equal(PayloadLength, total);
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private static byte[] Body(string signature1, string signature2, string signature3, string tail)
    {
        using var body = new MemoryStream();
        void Write(string text) => body.Write(Encoding.ASCII.GetBytes(text));

        Write($"10000;chunk-signature={signature1}\r\n");
        body.Write(Enumerable.Repeat((byte)'a', 65_536).ToArray());
        Write($"\r\n400;chunk-signature={signature2}\r\n");
        body.Write(Enumerable.Repeat((byte)'a', 1_024).ToArray());
        Write($"\r\n0;chunk-signature={signature3}\r\n{tail}");
        return body.ToArray();
    }
}
