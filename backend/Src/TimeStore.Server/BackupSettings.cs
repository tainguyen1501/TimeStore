using TimeStore.Application.Features.Backups;
using TimeStore.Blob.FileSystem;
using TimeStore.Domain.Backups;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Backup</c> (docs/backup.md).</summary>
public sealed class BackupSettings
{
    public const string Section = ServerOptions.Section + ":Backup";

    /// <summary>The folder of the data directory that holds archives when <see cref="Path"/> is not set.</summary>
    public const string DefaultFolder = "backups";
    public const int DefaultKeep = BackupOptions.DefaultKeep;
    public const int MaxKeep = 100;

    /// <summary>
    /// Where backups started from the admin API write their archives (the <c>directory</c> target). Unset:
    /// <c>&lt;DataPath&gt;/backups</c>, on the data volume. It is a place to take archives from (download them, later
    /// send them on), not a place to keep them: only <see cref="Keep"/> archives stay.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>How many archives the directory keeps: after a backup succeeds, older ones are deleted (1-100).</summary>
    public int Keep { get; set; } = DefaultKeep;

    /// <summary>Default compression: <c>zstd</c> (a <c>.tar.zst</c>) or <c>none</c> (a plain <c>.tar</c>).</summary>
    public string Compression { get; set; } = BackupCompressions.Zstd;

    /// <summary>Zstandard level 1-19: higher is smaller and slower (3 is zstd's own default).</summary>
    public int CompressionLevel { get; set; } = BackupOptions.DefaultCompressionLevel;

    /// <summary>
    /// A backup refuses to start with less free space in the data directory: blobs released while it runs stay on disk
    /// until it ends. 0 turns the check off.
    /// </summary>
    public long MinFreeBytes { get; set; } = BackupOptions.DefaultMinFreeBytes;

    /// <summary>The archive directory actually used.</summary>
    public string DirectoryPath(string dataPath) =>
        string.IsNullOrWhiteSpace(Path) ? System.IO.Path.Combine(dataPath, DefaultFolder) : Path;

    public string? Validate(string dataPath)
    {
        if (!BackupCompressions.IsKnown(Compression))
            return $"{Section}:{nameof(Compression)} must be '{BackupCompressions.Zstd}' or '{BackupCompressions.None}'.";
        if (CompressionLevel is < BackupOptions.MinCompressionLevel or > BackupOptions.MaxCompressionLevel)
            return $"{Section}:{nameof(CompressionLevel)} must be {BackupOptions.MinCompressionLevel}-{BackupOptions.MaxCompressionLevel}.";
        if (MinFreeBytes < 0)
            return $"{Section}:{nameof(MinFreeBytes)} must be 0 or more.";
        if (Keep is < 1 or > MaxKeep)
            return $"{Section}:{nameof(Keep)} must be 1-{MaxKeep}.";
        var directory = DirectoryPath(dataPath);
        if (IsSameOrInside(dataPath, directory)
            || IsSameOrInside(directory, System.IO.Path.Combine(dataPath, FileSystemBlobStore.BlobsDirectory))
            || IsSameOrInside(directory, System.IO.Path.Combine(dataPath, FileSystemBlobStore.TempDirectory)))
        {
            return $"{Section}:{nameof(Path)} must be a folder of its own: not the data directory, a folder holding it, "
                + $"nor its {FileSystemBlobStore.BlobsDirectory}/ or {FileSystemBlobStore.TempDirectory}/ (default: <DataPath>/{DefaultFolder}).";
        }

        return null;
    }

    private static bool IsSameOrInside(string path, string directory)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        return full.Equals(root, StringComparison.Ordinal)
            || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
