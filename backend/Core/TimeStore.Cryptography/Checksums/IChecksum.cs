namespace TimeStore.Cryptography.Checksums;

/// <summary>
/// An incremental checksum. Not thread-safe; create one per stream. Digests are written in canonical byte order:
/// big-endian for CRCs (the order Amazon S3 base64-encodes), natural order for SHA.
/// </summary>
public interface IChecksum : IDisposable
{
    ChecksumAlgorithm Algorithm { get; }

    /// <summary>Digest length in bytes.</summary>
    int Length { get; }

    void Append(ReadOnlySpan<byte> data);

    /// <summary>Writes the digest to <paramref name="destination"/> (at least <see cref="Length"/> bytes) and resets.</summary>
    /// <returns>The number of bytes written.</returns>
    int GetHashAndReset(Span<byte> destination);
}

/// <summary>Creates checksums. The one entry point to the checksum library (swap the implementation here).</summary>
public interface IChecksumFactory
{
    IChecksum Create(ChecksumAlgorithm algorithm);
}
