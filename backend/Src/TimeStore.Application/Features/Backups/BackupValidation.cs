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

    /// <summary>What the archive holds (full when null), or the error; metadata only needs a blob retention.</summary>
    public static Result<string> Content(string? requested, BackupOptions options)
    {
        var content = requested ?? BackupContents.Full;
        if (!BackupContents.IsKnown(content))
            return BackupErrors.InvalidContent;
        if (content == BackupContents.Metadata && options.BlobRetention <= TimeSpan.Zero)
            return BackupErrors.RetentionRequired;
        return content;
    }
}
