using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Primitives;

namespace TimeStore.Infra.Backups;

/// <summary>
/// An archive opened for a restore, read up to its checked manifest: the reader is positioned on the entry after
/// <c>manifest.json</c>. Disposing it closes the reader, then the source.
/// </summary>
public sealed class OpenedBackupArchive(Stream source, IBackupArchiveReader reader, BackupManifest manifest) : IAsyncDisposable
{
    public IBackupArchiveReader Reader => reader;

    public BackupManifest Manifest => manifest;

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await source.DisposeAsync();
    }
}

/// <summary>
/// The start every restore shares (a full one into an empty directory, a metadata one in place): open the archive (a
/// file or an <c>http(s)://</c> URL), then refuse what this restore cannot take before anything is written. The manifest
/// must come first and be readable; then its format, its content and its metadata schema are checked, in that order.
/// </summary>
public static class BackupArchiveOpener
{
    private const int MaxManifestBytes = 1 << 20;

    /// <summary>The error of an archive whose tar structure or Zstandard frame (or its checksum) is broken.</summary>
    public static AppError Damaged => BackupErrors.RestoreInvalidArchive("it is damaged or truncated.");

    /// <param name="content">The content this restore takes (<c>Domain.Backups.BackupContents</c>).</param>
    /// <param name="wrongContentHint">What to do with an archive of another content (ends the error message).</param>
    /// <param name="maxSchemaVersion">The newest metadata schema this build knows.</param>
    public static async ValueTask<Result<OpenedBackupArchive>> OpenAsync(
        IBackupArchiveFormat format, string archive, string content, string wrongContentHint, int maxSchemaVersion,
        CancellationToken ct)
    {
        var opened = await ArchiveSource.OpenAsync(archive, ct);
        if (opened.IsFailure)
            return opened.Error;

        var source = opened.Value;
        IBackupArchiveReader? reader = null;
        var handedOver = false;
        try
        {
            reader = await format.OpenReaderAsync(source, ct);
            var manifest = await ReadManifestAsync(format, reader, content, wrongContentHint, maxSchemaVersion, ct);
            if (manifest.IsFailure)
                return manifest.Error;
            handedOver = true;
            return new OpenedBackupArchive(source, reader, manifest.Value);
        }
        finally
        {
            if (!handedOver)
            {
                if (reader is not null)
                    await reader.DisposeAsync();
                await source.DisposeAsync();
            }
        }
    }

    private static async ValueTask<Result<BackupManifest>> ReadManifestAsync(
        IBackupArchiveFormat format, IBackupArchiveReader reader, string content, string wrongContentHint,
        int maxSchemaVersion, CancellationToken ct)
    {
        var first = await reader.NextAsync(ct);
        if (first is null || first.Name != BackupLayout.ManifestEntry || first.Length > MaxManifestBytes)
            return BackupErrors.RestoreInvalidArchive($"the first entry is not {BackupLayout.ManifestEntry}.");
        using var bytes = new MemoryStream();
        await first.CopyToAsync(bytes, ct);
        if (format.ParseManifest(bytes.ToArray()) is not { Format: >= 1 } manifest)
            return BackupErrors.RestoreInvalidArchive($"{BackupLayout.ManifestEntry} cannot be read.");
        if (manifest.Format > BackupLayout.FormatVersion)
            return BackupErrors.RestoreUnsupported(
                $"archive format {manifest.Format} is newer than this build reads ({BackupLayout.FormatVersion}); use a newer TimeStore.");
        if (manifest.Content != content)
            return BackupErrors.RestoreWrongContent(manifest.Content, wrongContentHint);
        if (manifest.SchemaVersion > maxSchemaVersion)
            return BackupErrors.RestoreUnsupported(
                $"metadata schema {manifest.SchemaVersion} is newer than this build knows ({maxSchemaVersion}); use a newer TimeStore.");
        return manifest;
    }
}
