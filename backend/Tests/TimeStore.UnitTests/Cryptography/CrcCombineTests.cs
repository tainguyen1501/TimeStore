using TimeStore.Cryptography.Checksums;

namespace TimeStore.UnitTests.Cryptography;

public sealed class CrcCombineTests
{
    private static readonly ChecksumFactory Factory = new();

    private static byte[] Crc(ChecksumAlgorithm algorithm, ReadOnlySpan<byte> data)
    {
        using var checksum = Factory.Create(algorithm);
        checksum.Append(data);
        var digest = new byte[checksum.Length];
        checksum.GetHashAndReset(digest);
        return digest;
    }

    public static TheoryData<ChecksumAlgorithm> Crcs => [ChecksumAlgorithm.Crc32, ChecksumAlgorithm.Crc32C, ChecksumAlgorithm.Crc64Nvme];

    [Theory]
    [MemberData(nameof(Crcs))]
    public void Combine_EqualsTheCrcOfTheConcatenation_ForRandomSplits(ChecksumAlgorithm algorithm)
    {
        var random = new Random(1234);
        for (var round = 0; round < 200; round++)
        {
            var data = new byte[random.Next(0, 5000)];
            random.NextBytes(data);
            var split = random.Next(0, data.Length + 1);
            var combined = new byte[Crc(algorithm, []).Length];

            CrcCombine.Combine(algorithm, Crc(algorithm, data.AsSpan(0, split)), Crc(algorithm, data.AsSpan(split)),
                data.Length - split, combined);

            Assert.Equal(Crc(algorithm, data), combined);
        }
    }

    [Theory]
    [MemberData(nameof(Crcs))]
    public void Fold_OverManyParts_FromTheEmptyCrc(ChecksumAlgorithm algorithm)
    {
        var random = new Random(99);
        var parts = Enumerable.Range(0, 25).Select(_ => { var p = new byte[random.Next(0, 3000)]; random.NextBytes(p); return p; }).ToArray();
        var running = Crc(algorithm, []);
        Assert.All(running, b => Assert.Equal(0, b)); // the CRC of nothing is zero: folding can start there

        foreach (var part in parts)
            CrcCombine.Combine(algorithm, running, Crc(algorithm, part), part.Length, running);

        Assert.Equal(Crc(algorithm, parts.SelectMany(p => p).ToArray()), running);
    }

    [Fact]
    public void Combine_HandlesLengthsBeyondMemory()
    {
        // crc(A ‖ zeros(n)) via combine vs combining n in two halves: exercises large exponents without allocating.
        var a = Crc(ChecksumAlgorithm.Crc64Nvme, "abc"u8);
        var zeros = Crc(ChecksumAlgorithm.Crc64Nvme, new byte[4096]);
        var once = new byte[8];
        var twice = new byte[8];
        CrcCombine.Combine(ChecksumAlgorithm.Crc64Nvme, a, zeros, 4096, once);
        CrcCombine.Combine(ChecksumAlgorithm.Crc64Nvme, once, zeros, 4096, twice);
        var direct = Crc(ChecksumAlgorithm.Crc64Nvme, [.. "abc"u8, .. new byte[8192]]);
        Assert.Equal(direct, twice);

        CrcCombine.Combine(ChecksumAlgorithm.Crc32, new byte[4], new byte[4], 5L * 1024 * 1024 * 1024 * 1024, new byte[4]); // 5 TiB: no overflow
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.Sha1)]
    [InlineData(ChecksumAlgorithm.Sha256)]
    public void Shas_CannotBeCombined(ChecksumAlgorithm algorithm)
    {
        Assert.False(CrcCombine.Supports(algorithm));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrcCombine.Combine(algorithm, new byte[20], new byte[20], 1, new byte[20]));
    }
}
