using System.Security.Cryptography;
using Amazon.S3.Model;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Streaming;
using TimeStore.UnitTests.Fakes;
using TimeStore.UnitTests.Protocol.Auth;
using SdkChecksum = Amazon.S3.ChecksumAlgorithm;

namespace TimeStore.UnitTests.Protocol.Streaming;

/// <summary>
/// Request bodies produced by the real AWS SDK for .NET for every checksum algorithm, verified end to end: SigV4 on the
/// headers, then <see cref="S3RequestBody"/> (the same decision the Server makes) yields exactly the uploaded bytes.
/// </summary>
public sealed class AwsSdkBodyTests : IDisposable
{
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(300_000); // several chunks

    private readonly AwsSdkCapture _sdk = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _sdk.Dispose();

    public static TheoryData<string> Algorithms() =>
    [
        SdkChecksum.CRC32.Value, SdkChecksum.CRC32C.Value, SdkChecksum.CRC64NVME.Value, SdkChecksum.SHA1.Value,
        SdkChecksum.SHA256.Value,
    ];

    [Theory]
    [MemberData(nameof(Algorithms))]
    public async Task PutObject_WithChecksum_YieldsPayload(string algorithm)
    {
        var (body, request) = await OpenAsync(algorithm);

        Assert.Equal(Payload, await AwsChunkedDocExamplesTests.ReadAllAsync(body.Body));
        Assert.Equal(Payload.Length, body.PayloadLength);
        Assert.Contains(algorithm.ToLowerInvariant(), request.GetHeader("x-amz-trailer") ?? string.Empty);
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public async Task PutObject_TamperedPayload_IsRejected(string algorithm)
    {
        var (body, _) = await OpenAsync(algorithm, tamper: true);

        var error = await Assert.ThrowsAsync<S3BodyVerificationException>(
            () => AwsChunkedDocExamplesTests.ReadAllAsync(body.Body));
        Assert.Equal(S3BodyErrors.Codes.SignatureMismatch, error.Error.Code);
    }

    private async Task<(S3Body Body, FakeSigV4Request Request)> OpenAsync(string algorithm, bool tamper = false)
    {
        var captured = await _sdk.CaptureAsync(() => _sdk.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "photos",
            Key = "data.bin",
            InputStream = new MemoryStream(Payload),
            ChecksumAlgorithm = SdkChecksum.FindValue(algorithm),
        }, _ct));

        var principal = await SigV4AwsSdkTests.Verifier().VerifyAsync(captured.Request, _ct);
        Assert.True(principal.IsSuccess, principal.IsFailure ? principal.Error.ToString() : null);

        var bytes = captured.Body.ToArray();
        if (tamper)
            bytes[bytes.Length / 2] ^= 0xFF; // inside the payload of the first chunk

        var opened = S3RequestBody.Open(
            new MemoryStream(bytes), bytes.Length, principal.Value, captured.Request, new ChecksumFactory());
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.ToString() : null);
        return (opened.Value, captured.Request);
    }
}
