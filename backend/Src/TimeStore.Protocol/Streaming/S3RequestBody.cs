using System.Globalization;
using TimeStore.Cryptography.Checksums;
using TimeStore.Primitives;
using TimeStore.Protocol.Auth;

namespace TimeStore.Protocol.Streaming;

/// <summary>A checksum the body was verified against (algorithm and digest in canonical byte order).</summary>
public sealed record VerifiedChecksum(ChecksumAlgorithm Algorithm, byte[] Digest);

/// <summary>The request body to hand to handlers: verifying on read, and the real payload length.</summary>
/// <param name="Body">Yields the payload (decoded for aws-chunked) and throws on any integrity failure.</param>
/// <param name="PayloadLength">Payload size when known (<c>x-amz-decoded-content-length</c> or Content-Length).</param>
public sealed record S3Body(Stream Body, long? PayloadLength)
{
    /// <summary>
    /// The checksum (header or trailer) the payload was verified against, available once the body has been read to
    /// its end; null when the client sent none.
    /// </summary>
    public VerifiedChecksum? VerifiedChecksum => Body switch
    {
        AwsChunkedStream chunked => chunked.VerifiedChecksum,
        PayloadVerifyingStream verifying => verifying.VerifiedChecksum,
        _ => null,
    };
}

/// <summary>
/// Chooses how to verify a request body from the authenticated payload mode and headers. Pure: the Server's body
/// middleware and the tests use the same decision.
/// </summary>
public static class S3RequestBody
{
    /// <param name="headerChecksumsDescribeBody">
    /// False for CompleteMultipartUpload: there <c>x-amz-checksum-*</c> headers carry the checksum of the whole object
    /// (checked by the completion), not of the XML body. Trailer checksums always describe the body.
    /// </param>
    public static Result<S3Body> Open(
        Stream body, long? contentLength, SigV4Principal principal, ISigV4Request headers, IChecksumFactory checksums,
        bool headerChecksumsDescribeBody = true)
    {
        switch (principal.PayloadMode)
        {
            case PayloadMode.StreamingSigned:
            case PayloadMode.StreamingSignedTrailer:
            case PayloadMode.StreamingUnsignedTrailer:
                return OpenChunked(body, principal, headers, checksums, headerChecksumsDescribeBody);

            case PayloadMode.Sha256:
            case PayloadMode.Unsigned:
            {
                var declared = headerChecksumsDescribeBody ? DeclaredChecksum(headers) : NoDeclaredChecksum;
                if (declared.IsFailure)
                    return declared.Error;
                if (principal.PayloadSha256 is null && declared.Value is null)
                    return new S3Body(body, contentLength);

                var (algorithm, digest) = declared.Value ?? default;
                var verifying = new PayloadVerifyingStream(
                    body, principal.PayloadSha256, declared.Value is null ? null : checksums.Create(algorithm), digest,
                    contentLength);
                return new S3Body(verifying, contentLength);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(principal), principal.PayloadMode, null);
        }
    }

    private static readonly Result<(ChecksumAlgorithm Algorithm, byte[] Digest)?> NoDeclaredChecksum =
        ((ChecksumAlgorithm, byte[])?)null;

    private static Result<S3Body> OpenChunked(
        Stream body, SigV4Principal principal, ISigV4Request headers, IChecksumFactory checksums,
        bool headerChecksumsDescribeBody)
    {
        if (!long.TryParse(headers.GetHeader(S3BodyNames.DecodedContentLength), NumberStyles.None,
                CultureInfo.InvariantCulture, out var decodedLength))
            return S3BodyErrors.MissingDecodedLength;

        var declared = headerChecksumsDescribeBody ? DeclaredChecksum(headers) : NoDeclaredChecksum;
        if (declared.IsFailure)
            return declared.Error;

        ChecksumAlgorithm? trailer = null;
        if (principal.PayloadMode != PayloadMode.StreamingSigned)
        {
            var announced = headers.GetHeader(S3BodyNames.Trailer)?.Trim();
            if (announced is null || !S3Checksums.TryFromHeaderName(announced, out var algorithm))
                return S3BodyErrors.InvalidTrailer($"{S3BodyNames.Trailer} must name one x-amz-checksum-* header.");
            if (declared.Value is not null)
                return S3BodyErrors.InvalidTrailer("a checksum header and a checksum trailer cannot both be sent.");
            trailer = algorithm;
        }

        var settings = new AwsChunkedSettings(principal.PayloadMode, decodedLength, trailer, principal);
        Stream decoded = new AwsChunkedStream(body, settings, checksums);

        // Signed chunks without a trailer may still declare a checksum header: verify it on the decoded payload.
        if (declared.Value is { } header)
            decoded = new PayloadVerifyingStream(
                decoded, null, checksums.Create(header.Algorithm), header.Digest, decodedLength);
        return new S3Body(decoded, decodedLength);
    }

    /// <summary>An <c>x-amz-checksum-*</c> request header on a plain body (at most one).</summary>
    private static Result<(ChecksumAlgorithm Algorithm, byte[] Digest)?> DeclaredChecksum(ISigV4Request headers)
    {
        (ChecksumAlgorithm, byte[])? found = null;
        foreach (var name in headers.HeaderNames)
        {
            if (!S3Checksums.TryFromHeaderName(name, out var algorithm))
                continue;
            if (found is not null || !S3Checksums.TryDecode(headers.GetHeader(name) ?? string.Empty, algorithm, out var digest))
                return S3BodyErrors.InvalidChecksum(name);
            found = (algorithm, digest);
        }

        return found;
    }
}
