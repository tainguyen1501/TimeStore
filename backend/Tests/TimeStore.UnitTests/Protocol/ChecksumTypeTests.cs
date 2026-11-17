using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Streaming;

namespace TimeStore.UnitTests.Protocol;

public sealed class ChecksumTypeTests
{
    [Theory]
    [InlineData("CRC32", null, ChecksumAlgorithm.Crc32, "COMPOSITE")]
    [InlineData("crc32c", "full_object", ChecksumAlgorithm.Crc32C, "FULL_OBJECT")]
    [InlineData("CRC64NVME", null, ChecksumAlgorithm.Crc64Nvme, "FULL_OBJECT")]
    [InlineData("SHA256", "COMPOSITE", ChecksumAlgorithm.Sha256, "COMPOSITE")]
    public void Resolve_AppliesAwsDefaults(string algorithm, string? type, ChecksumAlgorithm expected, string expectedType)
    {
        var resolved = S3ChecksumTypes.Resolve(algorithm, type).Value!.Value;

        Assert.Equal((expected, expectedType), (resolved.Algorithm, resolved.Type));
    }

    [Theory]
    [InlineData("SHA1", "FULL_OBJECT")]
    [InlineData("CRC64NVME", "COMPOSITE")]
    [InlineData("MD5", null)]
    [InlineData(null, "COMPOSITE")]
    [InlineData("CRC32", "WHOLE")]
    public void Resolve_RejectsCombinationsAwsRejects(string? algorithm, string? type) =>
        Assert.True(S3ChecksumTypes.Resolve(algorithm, type).IsFailure);

    [Fact]
    public void Resolve_WithoutHeaders_IsNoChecksum() => Assert.Null(S3ChecksumTypes.Resolve(null, null).Value);
}
