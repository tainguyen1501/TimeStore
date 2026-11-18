using TimeStore.Application.Features.Backups;
using TimeStore.Domain.Backups;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Backup</c> (docs/backup.md).</summary>
public sealed class BackupSettings
{
    public const string Section = ServerOptions.Section + ":Backup";

    /// <summary>
    /// Directory for the <c>directory</c> target: archives started from the admin API are written here. Outside the
    /// data directory (a second volume or a share). Unset: only streamed backups (<c>GET /api/v1/backups/stream</c>).
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Default compression: <c>zstd</c> (a <c>.tar.zst</c>) or <c>none</c> (a plain <c>.tar</c>).</summary>
    public string Compression { get; set; } = BackupCompressions.Zstd;

    /// <summary>Zstandard level 1-19: higher is smaller and slower (3 is zstd's own default).</summary>
    public int CompressionLevel { get; set; } = BackupOptions.DefaultCompressionLevel;

    /// <summary>
    /// A backup refuses to start with less free space in the data directory: blobs released while it runs stay on disk
    /// until it ends. 0 turns the check off.
    /// </summary>
    public long MinFreeBytes { get; set; } = BackupOptions.DefaultMinFreeBytes;

    public bool HasDirectory => !string.IsNullOrWhiteSpace(Path);

    public string? Validate(string dataPath)
    {
        if (!BackupCompressions.IsKnown(Compression))
            return $"{Section}:{nameof(Compression)} must be '{BackupCompressions.Zstd}' or '{BackupCompressions.None}'.";
        if (CompressionLevel is < BackupOptions.MinCompressionLevel or > BackupOptions.MaxCompressionLevel)
            return $"{Section}:{nameof(CompressionLevel)} must be {BackupOptions.MinCompressionLevel}-{BackupOptions.MaxCompressionLevel}.";
        if (MinFreeBytes < 0)
            return $"{Section}:{nameof(MinFreeBytes)} must be 0 or more.";
        if (HasDirectory && IsSameOrInside(Path!, dataPath))
            return $"{Section}:{nameof(Path)} must be outside the data directory (a copy on the same disk does not survive it).";
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
