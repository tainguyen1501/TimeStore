using TimeStore.Cryptography.Checksums;
using TimeStore.Primitives;
using TimeStore.Protocol.Routing;

namespace TimeStore.Protocol.Streaming;

/// <summary>
/// The checksum of a multipart upload, from CreateMultipartUpload's headers (Amazon S3 rules): SHA1/SHA256 are
/// composite only, CRC64NVME is full-object only, CRC32/CRC32C may be either (default composite).
/// </summary>
public static class S3ChecksumTypes
{
    public const string Composite = "COMPOSITE";
    public const string FullObject = "FULL_OBJECT";

    /// <summary>Null when the upload has no checksum; otherwise the algorithm and the type, defaults applied.</summary>
    public static Result<(ChecksumAlgorithm Algorithm, string Type)?> Resolve(string? algorithmHeader, string? typeHeader)
    {
        if (string.IsNullOrEmpty(algorithmHeader))
        {
            if (!string.IsNullOrEmpty(typeHeader))
                return S3RequestErrors.InvalidRequest($"{S3HeaderNames.ChecksumType} requires {S3HeaderNames.ChecksumAlgorithm}.");
            (ChecksumAlgorithm, string)? none = null;
            return none;
        }

        if (!ChecksumAlgorithms.TryParse(algorithmHeader, out var algorithm))
            return S3RequestErrors.InvalidRequest($"Checksum algorithm '{algorithmHeader}' is not supported.");

        var type = string.IsNullOrEmpty(typeHeader)
            ? algorithm == ChecksumAlgorithm.Crc64Nvme ? FullObject : Composite
            : typeHeader.ToUpperInvariant();
        var allowed = (algorithm, type) switch
        {
            (ChecksumAlgorithm.Crc32 or ChecksumAlgorithm.Crc32C, Composite or FullObject) => true,
            (ChecksumAlgorithm.Crc64Nvme, FullObject) => true,
            (ChecksumAlgorithm.Sha1 or ChecksumAlgorithm.Sha256, Composite) => true,
            _ => false,
        };
        return allowed
            ? (algorithm, type)
            : S3RequestErrors.InvalidRequest(
                $"The {ChecksumAlgorithms.NameOf(algorithm)} checksum algorithm does not support the {typeHeader} checksum type.");
    }
}
