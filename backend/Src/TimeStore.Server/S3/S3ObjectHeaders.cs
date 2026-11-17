using Microsoft.Net.Http.Headers;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Uploads;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;

namespace TimeStore.Server.S3;

/// <summary>Request headers every object write reads (PutObject, CreateMultipartUpload, UploadPart).</summary>
internal static class S3ObjectHeaders
{
    private const int Md5Length = 16;

    /// <summary>Content-MD5 as 16 bytes; null when absent; false when present but malformed (InvalidDigest).</summary>
    public static bool TryReadContentMd5(IHeaderDictionary headers, out byte[]? md5)
    {
        md5 = null;
        if (!headers.TryGetValue(S3HeaderNames.ContentMd5, out var value))
            return true;
        md5 = new byte[Md5Length];
        return Convert.TryFromBase64String(value.ToString(), md5, out var written) && written == Md5Length;
    }

    /// <summary>x-amz-meta-* headers, name lower-cased and without the prefix.</summary>
    public static List<ObjectHeader> UserMetadata(IHeaderDictionary headers)
    {
        var metadata = new List<ObjectHeader>();
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith(S3HeaderNames.UserMetadataPrefix, StringComparison.OrdinalIgnoreCase))
                metadata.Add(new ObjectHeader(name[S3HeaderNames.UserMetadataPrefix.Length..].ToLowerInvariant(), value.ToString()));
        }

        return metadata;
    }

    /// <summary>The system headers S3 stores with an object; aws-chunked is transport framing, not content encoding.</summary>
    public static List<ObjectHeader> StoredHeaders(IHeaderDictionary headers)
    {
        var stored = new List<ObjectHeader>();
        foreach (var name in S3HeaderNames.StoredObjectHeaders)
        {
            if (!headers.TryGetValue(name, out var value))
                continue;
            var text = value.ToString();
            if (name == S3BodyNames.ContentEncoding)
                text = string.Join(',', text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(token => !token.Equals(S3BodyNames.AwsChunked, StringComparison.OrdinalIgnoreCase)));
            if (text.Length > 0)
                stored.Add(new ObjectHeader(name, text));
        }

        return stored;
    }

    public static ObjectChecksum? ToObjectChecksum(VerifiedChecksum? verified) =>
        verified is null ? null : new ObjectChecksum(ChecksumAlgorithms.NameOf(verified.Algorithm), verified.Digest);

    public static string ChecksumHeader(string algorithm) =>
        ChecksumAlgorithms.TryParse(algorithm, out var parsed) ? S3Checksums.HeaderName(parsed) : algorithm;

    /// <summary>
    /// A stored checksum as S3 shows it: base64, plus <c>-N</c> (part count) for a composite multipart checksum.
    /// </summary>
    public static string ChecksumValue(byte[] digest, string? checksumType, int partsCount) =>
        checksumType == ChecksumTypes.Composite
            ? $"{Convert.ToBase64String(digest)}-{partsCount}"
            : Convert.ToBase64String(digest);
}
