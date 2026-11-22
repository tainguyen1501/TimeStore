using TimeStore.Domain.Backups;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Backups;

public static class BackupValidation
{
    /// <summary>The compression to use (the default when null), or the error.</summary>
    public static Result<string> Compression(string? requested, BackupOptions options)
    {
        var compression = requested ?? options.Compression;
        return BackupCompressions.IsKnown(compression) ? compression : BackupErrors.InvalidCompression;
    }

    /// <summary>
    /// What the archive holds (full when null), or the error. A metadata-only backup runs at any blob retention: the
    /// restore checks every blob it references, and the blobs may be kept elsewhere (a copy of <c>blobs/</c>, a volume
    /// snapshot); at retention 0 the server logs a warning (<see cref="HasNoBlobRetention"/>).
    /// </summary>
    public static Result<string> Content(string? requested)
    {
        var content = requested ?? BackupContents.Full;
        return BackupContents.IsKnown(content) ? content : BackupErrors.InvalidContent;
    }

    /// <summary>
    /// A metadata-only backup taken while released blobs are deleted at once: blobs overwritten or deleted after it are
    /// gone from this disk, so an in-place restore may miss them unless they are kept elsewhere.
    /// </summary>
    public static bool HasNoBlobRetention(string content, BackupOptions options) =>
        content == BackupContents.Metadata && options.BlobRetention <= TimeSpan.Zero;
}
