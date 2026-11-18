using TimeStore.Application.Features.Backups;

namespace TimeStore.UnitTests.Backups;

public sealed class BackupLayoutTests
{
    [Fact]
    public void BlobEntry_MirrorsTheDataDirectory_AndParsesBack()
    {
        var id = Guid.Parse("0196f3a1-2b3c-7d4e-8f90-a1b2c3d4e5f6");

        var entry = BackupLayout.BlobEntry(id);

        Assert.Equal("blobs/f6/0196f3a12b3c7d4e8f90a1b2c3d4e5f6", entry);
        Assert.True(BackupLayout.TryParseBlobEntry(entry, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("blobs/f5/0196f3a12b3c7d4e8f90a1b2c3d4e5f6")] // wrong shard
    [InlineData("blobs/F6/0196F3A12B3C7D4E8F90A1B2C3D4E5F6")] // upper case
    [InlineData("blobs/f6/0196f3a1-2b3c-7d4e-8f90-a1b2c3d4e5f6")]
    [InlineData("blobs/../0196f3a12b3c7d4e8f90a1b2c3d4e5f6xx")]
    [InlineData("blobs\\f6\\0196f3a12b3c7d4e8f90a1b2c3d4e5f6")]
    [InlineData("other/f6/0196f3a12b3c7d4e8f90a1b2c3d4e5f6")]
    [InlineData("blobs/f6/0196f3a12b3c7d4e8f90a1b2c3d4e5f6/x")]
    [InlineData("")]
    public void TryParseBlobEntry_RejectsEverythingElse(string entry) =>
        Assert.False(BackupLayout.TryParseBlobEntry(entry, out _));

    [Fact]
    public void ChecksumLine_IsSha256sumFormat_AndParsesBack()
    {
        var sha = new string('a', 64);

        var line = BackupLayout.ChecksumLine(sha, "metadata.db");

        Assert.Equal($"{sha}  metadata.db\n", line);
        Assert.True(BackupLayout.TryParseChecksumLine(line.TrimEnd('\n'), out var parsedSha, out var entry));
        Assert.Equal((sha, "metadata.db"), (parsedSha, entry));
        Assert.False(BackupLayout.TryParseChecksumLine($"{new string('A', 64)}  x", out _, out _));
        Assert.False(BackupLayout.TryParseChecksumLine($"{sha} x", out _, out _));
        Assert.False(BackupLayout.TryParseChecksumLine("short  x", out _, out _));
    }

    [Fact]
    public void ArchiveName_HasNodeAndUtcTime()
    {
        var name = BackupLayout.ArchiveName("node-1", new DateTime(2026, 11, 18, 2, 0, 5, DateTimeKind.Utc), ".tar.zst");

        Assert.Equal("timestore-node-1-20261118T020005Z.tar.zst", name);
    }
}
