using TimeStore.Cryptography.Checksums;

namespace TimeStore.Protocol.Streaming;

/// <summary>S3 names around request bodies and checksums.</summary>
public static class S3BodyNames
{
    public const string ContentEncoding = "content-encoding";
    public const string AwsChunked = "aws-chunked";
    public const string DecodedContentLength = "x-amz-decoded-content-length";
    public const string Trailer = "x-amz-trailer";
    public const string TrailerSignature = "x-amz-trailer-signature";
    public const string ChecksumHeaderPrefix = "x-amz-checksum-";
}

/// <summary>Maps checksum algorithms to their S3 header names (<c>x-amz-checksum-crc32c</c>).</summary>
public static class S3Checksums
{
    public static string HeaderName(ChecksumAlgorithm algorithm) =>
        S3BodyNames.ChecksumHeaderPrefix + ChecksumAlgorithms.NameOf(algorithm).ToLowerInvariant();

    public static bool TryFromHeaderName(ReadOnlySpan<char> headerName, out ChecksumAlgorithm algorithm)
    {
        algorithm = default;
        return headerName.StartsWith(S3BodyNames.ChecksumHeaderPrefix, StringComparison.OrdinalIgnoreCase)
            && ChecksumAlgorithms.TryParse(headerName[S3BodyNames.ChecksumHeaderPrefix.Length..], out algorithm);
    }

    /// <summary>Decodes a base64 checksum value of the right length for <paramref name="algorithm"/>.</summary>
    public static bool TryDecode(string base64, ChecksumAlgorithm algorithm, out byte[] digest)
    {
        digest = new byte[ChecksumAlgorithms.LengthOf(algorithm)];
        return Convert.TryFromBase64String(base64, digest, out var written) && written == digest.Length;
    }
}
