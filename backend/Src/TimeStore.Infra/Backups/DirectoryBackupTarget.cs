using TimeStore.Application.Abstractions;
using TimeStore.Domain.Backups;

namespace TimeStore.Infra.Backups;

/// <param name="Path">Directory that receives the archives (default <c>&lt;DataPath&gt;/backups</c>).</param>
public sealed record DirectoryBackupOptions(string Path);

/// <summary>
/// Archives as plain files in one directory. An archive is written as <c>&lt;name&gt;.partial</c> and renamed when
/// complete, so a listed archive is always whole; a run that fails or is cancelled leaves nothing behind. The
/// directory and the archives are readable by the server's user only: an archive holds all the data.
/// </summary>
public sealed class DirectoryBackupTarget(DirectoryBackupOptions options) : IBackupArchiveStore
{
    public const string PartialExtension = ".partial";
    private const string PartialPattern = "*" + PartialExtension;
    private const int BufferSize = 1 << 20;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly string[] ArchiveExtensions = [TarBackupArchiveFormat.ZstdExtension, TarBackupArchiveFormat.TarExtension];

    public string Type => BackupTargetTypes.Directory;

    public ValueTask<IBackupUpload> OpenWriteAsync(string archiveName, CancellationToken ct)
    {
        var final = PathOf(archiveName);
        CreateDirectory();
        var partial = final + PartialExtension;
        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = BufferSize,
        };
        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = OwnerOnlyFile;
        var stream = new FileStream(partial, create);
        return ValueTask.FromResult<IBackupUpload>(new Upload(stream, partial, final));
    }

    public ValueTask<int> DeleteIncompleteAsync(CancellationToken ct)
    {
        var directory = new DirectoryInfo(options.Path);
        if (!directory.Exists)
            return ValueTask.FromResult(0);

        // Single node, called before any run starts: every partial file is from a run that crashed.
        var removed = 0;
        foreach (var leftover in directory.EnumerateFiles(PartialPattern))
        {
            leftover.Delete();
            removed++;
        }

        return ValueTask.FromResult(removed);
    }

    public ValueTask<IReadOnlyList<StoredBackupArchive>> ListAsync(CancellationToken ct)
    {
        var directory = new DirectoryInfo(options.Path);
        if (!directory.Exists)
            return ValueTask.FromResult<IReadOnlyList<StoredBackupArchive>>([]);

        var archives = directory.EnumerateFiles()
            .Where(file => IsArchiveName(file.Name))
            .Select(file => new StoredBackupArchive(file.Name, file.Length, file.LastWriteTimeUtc))
            .OrderByDescending(archive => archive.ModifiedAt)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<StoredBackupArchive>>(archives);
    }

    public ValueTask<Stream?> OpenReadAsync(string archiveName, CancellationToken ct)
    {
        var path = PathOf(archiveName);
        if (!File.Exists(path))
            return ValueTask.FromResult<Stream?>(null);
        try
        {
            return ValueTask.FromResult<Stream?>(new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = BufferSize,
            }));
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null); // deleted meanwhile
        }
    }

    public ValueTask<bool> DeleteAsync(string archiveName, CancellationToken ct)
    {
        var path = PathOf(archiveName);
        if (!File.Exists(path))
            return ValueTask.FromResult(false);
        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    public long? AvailableBytes()
    {
        try
        {
            CreateDirectory();
            return new DriveInfo(Path.GetFullPath(options.Path)).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // not measurable here: writing the archive will tell
        }
    }

    private void CreateDirectory()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(options.Path);
        else if (!Directory.Exists(options.Path))
            Directory.CreateDirectory(options.Path, OwnerOnlyDirectory);
    }

    /// <summary>Only plain archive names map to a path in the directory (never a separator, never "..").</summary>
    private string PathOf(string archiveName) =>
        IsArchiveName(archiveName)
            ? Path.Combine(options.Path, archiveName)
            : throw new ArgumentException($"'{archiveName}' is not an archive name.", nameof(archiveName));

    private static bool IsArchiveName(string name) =>
        name.Length > 0 && name[0] != '.' && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && ArchiveExtensions.Any(extension => name.EndsWith(extension, StringComparison.Ordinal));

    private sealed class Upload(FileStream stream, string partial, string final) : IBackupUpload
    {
        private bool _committed;

        public Stream Stream => stream;

        public async ValueTask CommitAsync(CancellationToken ct)
        {
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
            await stream.DisposeAsync();
            File.Move(partial, final, overwrite: false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            if (!_committed)
                File.Delete(partial);
        }
    }
}
