using System.Text;
using TimeStore.Cryptography;
using TimeStore.Cryptography.Checksums;

namespace TimeStore.UnitTests.Cryptography;

public sealed class ChecksumFactoryTests
{
    private static readonly byte[] CheckInput = "123456789"u8.ToArray();
    private readonly ChecksumFactory _factory = new();

    // Standard "check" values of each algorithm for the input "123456789" (canonical big-endian for CRCs).
    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, "cbf43926")]
    [InlineData(ChecksumAlgorithm.Crc32C, "e3069283")]
    [InlineData(ChecksumAlgorithm.Crc64Nvme, "ae8b14860a799888")]
    [InlineData(ChecksumAlgorithm.Sha1, "f7c3bc1d808e04732adf679965ccc34ca7ae3441")]
    [InlineData(ChecksumAlgorithm.Sha256, "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225")]
    public void Checksum_OfCheckInput_MatchesKnownValue(ChecksumAlgorithm algorithm, string expectedHex)
    {
        using var checksum = _factory.Create(algorithm);
        checksum.Append(CheckInput);

        var digest = new byte[checksum.Length];
        var written = checksum.GetHashAndReset(digest);

        Assert.Equal(ChecksumAlgorithms.LengthOf(algorithm), written);
        Assert.Equal(expectedHex, Convert.ToHexStringLower(digest));
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32)]
    [InlineData(ChecksumAlgorithm.Crc32C)]
    [InlineData(ChecksumAlgorithm.Crc64Nvme)]
    [InlineData(ChecksumAlgorithm.Sha1)]
    [InlineData(ChecksumAlgorithm.Sha256)]
    public void Checksum_AppendedInPieces_EqualsOneShot_AndResets(ChecksumAlgorithm algorithm)
    {
        using var checksum = _factory.Create(algorithm);
        var oneShot = new byte[checksum.Length];
        checksum.Append(CheckInput);
        checksum.GetHashAndReset(oneShot);

        foreach (var b in CheckInput)
            checksum.Append([b]);
        var pieces = new byte[checksum.Length];
        checksum.GetHashAndReset(pieces);

        Assert.Equal(oneShot, pieces);
    }

    [Theory]
    [InlineData("crc64nvme", ChecksumAlgorithm.Crc64Nvme)]
    [InlineData("CRC32C", ChecksumAlgorithm.Crc32C)]
    [InlineData("sha256", ChecksumAlgorithm.Sha256)]
    public void TryParse_AcceptsNamesCaseInsensitively(string name, ChecksumAlgorithm expected)
    {
        Assert.True(ChecksumAlgorithms.TryParse(name, out var algorithm));
        Assert.Equal(expected, algorithm);
    }

    [Fact]
    public void TryParse_RejectsUnknownName() => Assert.False(ChecksumAlgorithms.TryParse("MD5", out _));

    // RFC 4231 test case 2.
    [Fact]
    public void HmacSha256_MatchesRfc4231()
    {
        Span<byte> mac = stackalloc byte[Signing.Sha256Length];

        Signing.HmacSha256(Encoding.ASCII.GetBytes("Jefe"), Encoding.ASCII.GetBytes("what do ya want for nothing?"), mac);

        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", Convert.ToHexStringLower(mac));
    }

    [Fact]
    public void FixedTimeEquals_ComparesContent()
    {
        Assert.True(Signing.FixedTimeEquals([1, 2, 3], [1, 2, 3]));
        Assert.False(Signing.FixedTimeEquals([1, 2, 3], [1, 2, 4]));
        Assert.False(Signing.FixedTimeEquals([1, 2, 3], [1, 2]));
    }
}
