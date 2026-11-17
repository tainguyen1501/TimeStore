using System.Buffers.Binary;

namespace TimeStore.Cryptography.Checksums;

/// <summary>
/// The CRC of a concatenation from the CRCs of its pieces and their lengths, without reading the data again
/// (multipart <c>FULL_OBJECT</c> checksums). Works on the three reflected CRCs Amazon S3 combines (CRC32, CRC32C,
/// CRC64NVME).
/// </summary>
/// <remarks>
/// Math: with a linear CRC <c>r</c>, <c>crc(M) = r(M) ⊕ I·x^(8|M|) ⊕ X</c> (init <c>I</c>, final xor <c>X</c>), so
/// <c>crc(A‖B) = crc(A)·x^(8|B|) ⊕ crc(B)</c> exactly when <c>I = X</c>; all three use all-ones for both.
/// Multiplication is carry-less modulo the polynomial, in the reflected bit order the CRCs are computed in;
/// <c>x^(8n)</c> is built from a table of <c>x^(2^k)</c> by squaring, so a combine costs O(log n) multiplications.
/// </remarks>
public static class CrcCombine
{
    private static readonly Parameters Crc32 = new(32, 0xEDB88320UL);
    private static readonly Parameters Crc32C = new(32, 0x82F63B78UL);
    private static readonly Parameters Crc64Nvme = new(64, 0x9A6C9329AC4BC9B5UL);

    public static bool Supports(ChecksumAlgorithm algorithm) =>
        algorithm is ChecksumAlgorithm.Crc32 or ChecksumAlgorithm.Crc32C or ChecksumAlgorithm.Crc64Nvme;

    /// <summary>
    /// Combines digests in canonical byte order (big-endian, as <see cref="IChecksum"/> writes them):
    /// <paramref name="first"/> of the leading data, <paramref name="second"/> of the following
    /// <paramref name="secondLength"/> bytes. The CRC of no data is all zeros, so folding from zeros works.
    /// </summary>
    public static void Combine(
        ChecksumAlgorithm algorithm, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, long secondLength,
        Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(secondLength);
        var parameters = For(algorithm);
        var a = Read(parameters, first);
        var b = Read(parameters, second);
        var combined = parameters.Multiply(parameters.XPowerOfBytes(secondLength), a) ^ b;
        Write(parameters, combined, destination);
    }

    private static Parameters For(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => Crc32,
        ChecksumAlgorithm.Crc32C => Crc32C,
        ChecksumAlgorithm.Crc64Nvme => Crc64Nvme,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Only CRCs can be combined."),
    };

    private static ulong Read(Parameters parameters, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != parameters.Bytes)
            throw new ArgumentException($"A CRC-{parameters.Width} digest has {parameters.Bytes} bytes.", nameof(digest));
        return parameters.Width == 32 ? BinaryPrimitives.ReadUInt32BigEndian(digest) : BinaryPrimitives.ReadUInt64BigEndian(digest);
    }

    private static void Write(Parameters parameters, ulong value, Span<byte> destination)
    {
        if (parameters.Width == 32)
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value);
        else
            BinaryPrimitives.WriteUInt64BigEndian(destination, value);
    }

    /// <summary>One reflected CRC: width, reflected polynomial and the table of <c>x^(2^k)</c>.</summary>
    private sealed class Parameters
    {
        private const int TableSize = 64; // enough for any long length in bits
        private readonly ulong _polynomial;
        private readonly ulong _top; // the x^0 term: the highest bit in reflected order
        private readonly ulong[] _powers = new ulong[TableSize]; // _powers[k] = x^(2^k) mod P

        public Parameters(int width, ulong reflectedPolynomial)
        {
            Width = width;
            _polynomial = reflectedPolynomial;
            _top = 1UL << (width - 1);
            _powers[0] = _top >> 1; // x^1
            for (var k = 1; k < TableSize; k++)
                _powers[k] = Multiply(_powers[k - 1], _powers[k - 1]);
        }

        public int Width { get; }

        public int Bytes => Width / 8;

        /// <summary><c>a·b mod P</c>, carry-less, reflected bit order.</summary>
        public ulong Multiply(ulong a, ulong b)
        {
            var product = 0UL;
            for (var bit = _top; bit != 0 && a != 0; bit >>= 1)
            {
                if ((a & bit) != 0)
                {
                    product ^= b;
                    a ^= bit;
                }

                b = (b & 1) != 0 ? (b >> 1) ^ _polynomial : b >> 1;
            }

            return product;
        }

        /// <summary><c>x^(8·bytes) mod P</c>: bytes·8 = bytes·2^3, so start at table entry 3.</summary>
        public ulong XPowerOfBytes(long bytes)
        {
            var result = _top; // x^0
            for (var k = 3; bytes != 0; k++, bytes >>= 1)
            {
                if ((bytes & 1) != 0)
                    result = Multiply(_powers[k % TableSize], result);
            }

            return result;
        }
    }
}
