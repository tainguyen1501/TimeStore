namespace TimeStore.Application.Abstractions;

/// <summary>Where a backup writes its archive (docs/research/backup.md section 5). One class per destination.</summary>
public interface IBackupTarget
{
    /// <summary><c>Domain.Backups.BackupTargetTypes</c> value.</summary>
    string Type { get; }

    /// <summary>
    /// Starts writing an archive. Nothing is visible under <paramref name="archiveName"/> until the upload is
    /// committed; disposing an uncommitted upload removes what was written.
    /// </summary>
    ValueTask<IBackupUpload> OpenWriteAsync(string archiveName, CancellationToken ct);
}

/// <summary>A target that keeps archives, so they can be listed, downloaded, restored and deleted.</summary>
public interface IBackupArchiveStore : IBackupTarget
{
    /// <summary>Every complete archive, newest first.</summary>
    ValueTask<IReadOnlyList<StoredBackupArchive>> ListAsync(CancellationToken ct);

    /// <summary>Null when no archive has that name.</summary>
    ValueTask<Stream?> OpenReadAsync(string archiveName, CancellationToken ct);

    /// <summary>False when no archive has that name.</summary>
    ValueTask<bool> DeleteAsync(string archiveName, CancellationToken ct);

    /// <summary>Free space where archives are written, in bytes; null when it cannot be measured.</summary>
    long? AvailableBytes();

    /// <summary>
    /// Startup: removes what uploads left behind when the process died mid-backup (an upload disposed normally
    /// removes its own). Returns how many were removed.
    /// </summary>
    ValueTask<int> DeleteIncompleteAsync(CancellationToken ct);
}

public interface IBackupUpload : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>Makes the archive visible under its name (after the writer completed).</summary>
    ValueTask CommitAsync(CancellationToken ct);
}

public sealed record StoredBackupArchive(string Name, long Size, DateTime ModifiedAt);
