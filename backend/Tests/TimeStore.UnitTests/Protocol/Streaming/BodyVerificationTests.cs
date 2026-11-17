using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Streaming;
using static TimeStore.UnitTests.Protocol.Streaming.AwsChunkedDocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Streaming;

/// <summary>Every way a body can break its integrity promise, plus the unsigned and plain-body modes.</summary>
public sealed class BodyVerificationTests
{
    private static readonly byte[] Hello = "Hello, TimeStore!"u8.ToArray();

    private static async Task<string> FailCode(Stream stream)
    {
        var error = await Assert.ThrowsAsync<S3BodyVerificationException>(() => ReadAllAsync(stream));
        return error.Error.Code;
    }

    // --- Signed chunks -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TamperedPayloadByte_IsSignatureMismatch()
    {
        var body = SignedBody();
        body[100] = (byte)'b';

        Assert.Equal(S3BodyErrors.Codes.SignatureMismatch, await FailCode(SignedStream(body)));
    }

    [Fact]
    public async Task TamperedChunkSignature_IsSignatureMismatch()
    {
        var body = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(SignedBody()).Replace("ad80c730", "ad80c731"));

        Assert.Equal(S3BodyErrors.Codes.SignatureMismatch, await FailCode(SignedStream(body)));
    }

    [Fact]
    public async Task TruncatedBody_IsIncomplete() =>
        Assert.Equal(S3BodyErrors.Codes.Incomplete, await FailCode(SignedStream(SignedBody()[..40_000])));

    [Fact]
    public async Task DecodedLengthTooSmall_IsMalformed() =>
        Assert.Equal(S3BodyErrors.Codes.Malformed, await FailCode(SignedStream(SignedBody(), decodedLength: 1_000)));

    [Fact]
    public async Task DecodedLengthTooLarge_IsIncomplete() =>
        Assert.Equal(S3BodyErrors.Codes.Incomplete, await FailCode(SignedStream(SignedBody(), decodedLength: 70_000)));

    [Fact]
    public async Task NonHexChunkSize_IsMalformed()
    {
        var body = Encoding.ASCII.GetBytes("zz;chunk-signature=abc\r\n");

        Assert.Equal(S3BodyErrors.Codes.Malformed, await FailCode(SignedStream(body)));
    }

    [Fact]
    public async Task OverlongHeaderLine_IsMalformed()
    {
        var body = Encoding.ASCII.GetBytes("10000;" + new string('x', 5_000) + "\r\n");

        Assert.Equal(S3BodyErrors.Codes.Malformed, await FailCode(SignedStream(body)));
    }

    [Fact]
    public async Task MissingChunkSignatureInSignedMode_IsMalformed()
    {
        var body = Encoding.ASCII.GetBytes("11\r\nHello, TimeStore!\r\n0\r\n\r\n");

        Assert.Equal(S3BodyErrors.Codes.Malformed, await FailCode(SignedStream(body, decodedLength: Hello.Length)));
    }

    // --- Signed trailer ------------------------------------------------------------------------------------------

    [Fact]
    public async Task WrongTrailerChecksum_IsBadDigest() =>
        Assert.Equal(S3BodyErrors.Codes.BadDigest, await FailCode(SignedTrailerStream(SignedTrailerBody("AAAAAA=="))));

    [Fact]
    public async Task TamperedTrailerSignature_IsSignatureMismatch()
    {
        var body = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(SignedTrailerBody()).Replace("d81f82fc", "d81f82fd"));

        Assert.Equal(S3BodyErrors.Codes.SignatureMismatch, await FailCode(SignedTrailerStream(body)));
    }

    [Fact]
    public async Task MissingTrailerSignature_IsInvalidTrailer()
    {
        var text = Encoding.ASCII.GetString(SignedTrailerBody());
        var body = Encoding.ASCII.GetBytes(text[..text.IndexOf("x-amz-trailer-signature", StringComparison.Ordinal)] + "\r\n");

        Assert.Equal(S3BodyErrors.Codes.InvalidTrailer, await FailCode(SignedTrailerStream(body)));
    }

    [Fact]
    public async Task TrailerForOtherAlgorithm_IsInvalidTrailer()
    {
        var body = Encoding.ASCII.GetBytes(
            Encoding.ASCII.GetString(SignedTrailerBody()).Replace("x-amz-checksum-crc32c", "x-amz-checksum-sha1"));

        Assert.Equal(S3BodyErrors.Codes.InvalidTrailer, await FailCode(SignedTrailerStream(body)));
    }

    // --- Unsigned trailer (current AWS SDK default over HTTPS) ---------------------------------------------------

    private static AwsChunkedStream Unsigned(string body, ChecksumAlgorithm algorithm = ChecksumAlgorithm.Crc32) =>
        new(new MemoryStream(Encoding.ASCII.GetBytes(body)),
            new AwsChunkedSettings(PayloadMode.StreamingUnsignedTrailer, Hello.Length, algorithm,
                Principal("unused-in-unsigned-mode")),
            new ChecksumFactory());

    private static string Crc32Base64(byte[] data) => Convert.ToBase64String(BigEndian(Crc32.HashToUInt32(data)));

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    [Theory]
    [InlineData("\r\n\r\n")] // documented ending
    [InlineData("\r\n")] // some clients omit the final empty line
    [InlineData("\n\r\n")] // trailer line ending with a bare LF
    public async Task UnsignedTrailer_AcceptsClientVariants(string ending)
    {
        var body = $"11\r\nHello, TimeStore!\r\n0\r\nx-amz-checksum-crc32:{Crc32Base64(Hello)}{ending}";

        Assert.Equal(Hello, await ReadAllAsync(Unsigned(body)));
    }

    [Fact]
    public async Task UnsignedTrailer_IgnoresChunkSignatures() // sent by some SDK versions in unsigned mode
    {
        var body = $"11;chunk-signature=0000\r\nHello, TimeStore!\r\n0;chunk-signature=0000\r\nx-amz-checksum-crc32:{Crc32Base64(Hello)}\r\n\r\n";

        Assert.Equal(Hello, await ReadAllAsync(Unsigned(body)));
    }

    [Fact]
    public async Task UnsignedTrailer_WrongChecksum_IsBadDigest() =>
        Assert.Equal(S3BodyErrors.Codes.BadDigest,
            await FailCode(Unsigned("11\r\nHello, TimeStore!\r\n0\r\nx-amz-checksum-crc32:AAAAAA==\r\n\r\n")));

    [Fact]
    public async Task UnsignedTrailer_MissingTrailer_IsInvalidTrailer() =>
        Assert.Equal(S3BodyErrors.Codes.InvalidTrailer, await FailCode(Unsigned("11\r\nHello, TimeStore!\r\n0\r\n\r\n")));

    // --- Plain bodies --------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlainBody_MatchingSha256_Passes()
    {
        var stream = new PayloadVerifyingStream(new MemoryStream(Hello), Convert.ToHexStringLower(SHA256.HashData(Hello)), null, null, Hello.Length);

        Assert.Equal(Hello, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task PlainBody_WrongSha256_IsContentSha256Mismatch() =>
        Assert.Equal(S3BodyErrors.Codes.ContentSha256Mismatch,
            await FailCode(new PayloadVerifyingStream(new MemoryStream(Hello), SigV4Names.EmptySha256, null, null, Hello.Length)));

    [Fact]
    public async Task PlainBody_ChecksumHeader_IsVerified()
    {
        var factory = new ChecksumFactory();
        var good = new PayloadVerifyingStream(
            new MemoryStream(Hello), null, factory.Create(ChecksumAlgorithm.Crc32), BigEndian(Crc32.HashToUInt32(Hello)), null);
        var bad = new PayloadVerifyingStream(new MemoryStream(Hello), null, factory.Create(ChecksumAlgorithm.Crc32), [0, 0, 0, 0], null);

        Assert.Equal(Hello, await ReadAllAsync(good));
        Assert.Equal(S3BodyErrors.Codes.BadDigest, await FailCode(bad));
    }
}
