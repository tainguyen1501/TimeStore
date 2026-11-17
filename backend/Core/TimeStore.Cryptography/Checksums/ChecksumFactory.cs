using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace TimeStore.Cryptography.Checksums;

/// <summary>
/// <see cref="IChecksumFactory"/> on the .NET libraries: CRCs from <c>System.IO.Hashing</c> (vectorised), SHA from
/// <see cref="IncrementalHash"/>. Stateless; register as a singleton.
/// </summary>
public sealed class ChecksumFactory : IChecksumFactory
{
    public IChecksum Create(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => new Crc32Checksum(algorithm, new Crc32(Crc32ParameterSet.Crc32)),
        ChecksumAlgorithm.Crc32C => new Crc32Checksum(algorithm, new Crc32(Crc32ParameterSet.Crc32C)),
        ChecksumAlgorithm.Crc64Nvme => new Crc64Checksum(new Crc64(Crc64ParameterSet.Nvme)),
        ChecksumAlgorithm.Sha1 => new ShaChecksum(algorithm, IncrementalHash.CreateHash(HashAlgorithmName.SHA1)),
        ChecksumAlgorithm.Sha256 => new ShaChecksum(algorithm, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private sealed class Crc32Checksum(ChecksumAlgorithm algorithm, Crc32 crc) : IChecksum
    {
        public ChecksumAlgorithm Algorithm => algorithm;

        public int Length => sizeof(uint);

        public void Append(ReadOnlySpan<byte> data) => crc.Append(data);

        public int GetHashAndReset(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, crc.GetCurrentHashAsUInt32());
            crc.Reset();
            return sizeof(uint);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Crc64Checksum(Crc64 crc) : IChecksum
    {
        public ChecksumAlgorithm Algorithm => ChecksumAlgorithm.Crc64Nvme;

        public int Length => sizeof(ulong);

        public void Append(ReadOnlySpan<byte> data) => crc.Append(data);

        public int GetHashAndReset(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination, crc.GetCurrentHashAsUInt64());
            crc.Reset();
            return sizeof(ulong);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ShaChecksum(ChecksumAlgorithm algorithm, IncrementalHash hash) : IChecksum
    {
        public ChecksumAlgorithm Algorithm => algorithm;

        public int Length => hash.HashLengthInBytes;

        public void Append(ReadOnlySpan<byte> data) => hash.AppendData(data);

        public int GetHashAndReset(Span<byte> destination) => hash.GetHashAndReset(destination);

        public void Dispose() => hash.Dispose();
    }
}
