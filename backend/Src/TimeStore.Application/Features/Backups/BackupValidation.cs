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
}
