using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Streaming;
using TimeStore.UnitTests.Fakes;
using static TimeStore.UnitTests.Protocol.Streaming.AwsChunkedDocExamplesTests;

namespace TimeStore.UnitTests.Protocol.Streaming;

/// <summary>
/// A reader that stops after exactly the declared number of bytes (as a blob writer copying "length" bytes does) must
/// still get every integrity failure: the checks run in the read that returns the last payload byte.
/// </summary>
public sealed class ExactLengthReadTests
{
    private static readonly byte[] Hello = "Hello, TimeStore!"u8.ToArray();

    /// <summary>Reads exactly <paramref name="length"/> bytes and never asks for more.</summary>
    private static async Task ReadExactlyAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
    }

    private static async Task<string> FailCode(Stream stream, int length) =>
        (await Assert.ThrowsAsync<S3BodyVerificationException>(() => ReadExactlyAsync(stream, length))).Error.Code;

    [Fact]
    public async Task ValidBodies_PassWithExactReads()
    {
        await ReadExactlyAsync(SignedStream(SignedBody()), PayloadLength);
        await ReadExactlyAsync(SignedTrailerStream(SignedTrailerBody()), PayloadLength);
    }

    [Fact]
    public async Task TamperedFinalChunkSignature_FailsWithoutReadingPastThePayload()
    {
        var body = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(SignedBody()).Replace("b6c6ea8a", "b6c6ea8b"));

        Assert.Equal(S3BodyErrors.Codes.SignatureMismatch, await FailCode(SignedStream(body), PayloadLength));
    }

    [Fact]
    public async Task TamperedLastDataChunk_FailsWithoutReadingPastThePayload()
    {
        var body = SignedBody();
        body[body.Length - 200] = (byte)'b'; // inside the 1 KiB chunk

        Assert.Equal(S3BodyErrors.Codes.SignatureMismatch, await FailCode(SignedStream(body), PayloadLength));
    }

    [Fact]
    public async Task WrongTrailerChecksum_FailsWithoutReadingPastThePayload() =>
        Assert.Equal(S3BodyErrors.Codes.BadDigest,
            await FailCode(SignedTrailerStream(SignedTrailerBody("AAAAAA==")), PayloadLength));

    [Fact]
    public async Task WrongContentSha256_FailsWithoutReadingPastThePayload()
    {
        var stream = new PayloadVerifyingStream(new MemoryStream(Hello), SigV4Names.EmptySha256, null, null, Hello.Length);

        Assert.Equal(S3BodyErrors.Codes.ContentSha256Mismatch, await FailCode(stream, Hello.Length));
    }

    [Fact]
    public async Task TooManyTrailerLines_IsInvalidTrailer()
    {
        var text = Encoding.ASCII.GetString(SignedTrailerBody());
        var extra = string.Concat(Enumerable.Repeat("x-amz-meta-pad:1\r\n", 10));
        var body = Encoding.ASCII.GetBytes(text.Replace("x-amz-checksum-crc32c", extra + "x-amz-checksum-crc32c"));

        Assert.Equal(S3BodyErrors.Codes.InvalidTrailer, await FailCode(SignedTrailerStream(body), PayloadLength));
    }

    // --- Checksum header together with aws-chunked ---------------------------------------------------------------

    private static FakeSigV4Request ChunkedHeaders(params (string Name, string Value)[] extra)
    {
        var headers = new Dictionary<string, string> { ["x-amz-decoded-content-length"] = PayloadLength.ToString() };
        foreach (var (name, value) in extra)
            headers[name] = value;
        return new FakeSigV4Request("PUT", "/examplebucket/chunkObject.txt", headers);
    }

    private static string Crc32Of65KiB() => Convert.ToBase64String(
        BitConverter.GetBytes(Crc32.HashToUInt32(Enumerable.Repeat((byte)'a', PayloadLength).ToArray())).Reverse().ToArray());

    [Fact]
    public async Task SignedChunks_WithChecksumHeader_VerifyTheHeader()
    {
        var principal = Principal("4f232c4386841ef735655705268965c44a0e4690baa4adea153f7db9fa80a0a9");

        var good = S3RequestBody.Open(new MemoryStream(SignedBody()), null, principal,
            ChunkedHeaders(("x-amz-checksum-crc32", Crc32Of65KiB())), new ChecksumFactory());
        var bad = S3RequestBody.Open(new MemoryStream(SignedBody()), null, principal,
            ChunkedHeaders(("x-amz-checksum-crc32", "AAAAAA==")), new ChecksumFactory());

        await ReadExactlyAsync(good.Value.Body, PayloadLength);
        Assert.Equal(S3BodyErrors.Codes.BadDigest, await FailCode(bad.Value.Body, PayloadLength));
    }

    [Fact]
    public void ChecksumHeaderAndTrailer_AreRejected()
    {
        var principal = Principal("106e2a8a18243abcf37539882f36619c00e2dfc72633413f02d3b74544bfeb8e") with
        {
            PayloadMode = PayloadMode.StreamingSignedTrailer,
        };

        var opened = S3RequestBody.Open(new MemoryStream(SignedTrailerBody()), null, principal,
            ChunkedHeaders(("x-amz-trailer", "x-amz-checksum-crc32c"), ("x-amz-checksum-crc32c", "sOO8/Q==")),
            new ChecksumFactory());

        Assert.Equal(S3BodyErrors.Codes.InvalidTrailer, opened.Error.Code);
    }

    [Fact]
    public async Task PlainBody_ShorterThanContentLength_IsIncomplete()
    {
        var stream = new PayloadVerifyingStream(
            new MemoryStream(Hello), Convert.ToHexStringLower(SHA256.HashData(Hello)), null, null, Hello.Length + 5);

        var error = await Assert.ThrowsAsync<S3BodyVerificationException>(() => ReadAllAsync(stream));
        Assert.Equal(S3BodyErrors.Codes.Incomplete, error.Error.Code);
    }
}
