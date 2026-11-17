namespace TimeStore.Cryptography.Checksums;

/// <summary>Checksum algorithms used for data integrity (the set Amazon S3 supports).</summary>
public enum ChecksumAlgorithm
{
    Crc32,
    Crc32C,
    Crc64Nvme,
    Sha1,
    Sha256,
}

public static class ChecksumAlgorithms
{
    public const string Crc32Name = "CRC32";
    public const string Crc32CName = "CRC32C";
    public const string Crc64NvmeName = "CRC64NVME";
    public const string Sha1Name = "SHA1";
    public const string Sha256Name = "SHA256";

    /// <summary>Digest length in bytes.</summary>
    public static int LengthOf(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 or ChecksumAlgorithm.Crc32C => sizeof(uint),
        ChecksumAlgorithm.Crc64Nvme => sizeof(ulong),
        ChecksumAlgorithm.Sha1 => 20,
        ChecksumAlgorithm.Sha256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>The canonical upper-case name (<c>CRC32C</c>).</summary>
    public static string NameOf(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => Crc32Name,
        ChecksumAlgorithm.Crc32C => Crc32CName,
        ChecksumAlgorithm.Crc64Nvme => Crc64NvmeName,
        ChecksumAlgorithm.Sha1 => Sha1Name,
        ChecksumAlgorithm.Sha256 => Sha256Name,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>Parses a name case-insensitively (<c>crc32c</c>, <c>CRC64NVME</c>).</summary>
    public static bool TryParse(ReadOnlySpan<char> name, out ChecksumAlgorithm algorithm)
    {
        foreach (var candidate in All)
        {
            if (name.Equals(NameOf(candidate), StringComparison.OrdinalIgnoreCase))
            {
                algorithm = candidate;
                return true;
            }
        }

        algorithm = default;
        return false;
    }

    public static ReadOnlySpan<ChecksumAlgorithm> All =>
    [
        ChecksumAlgorithm.Crc32, ChecksumAlgorithm.Crc32C, ChecksumAlgorithm.Crc64Nvme, ChecksumAlgorithm.Sha1,
        ChecksumAlgorithm.Sha256,
    ];
}
