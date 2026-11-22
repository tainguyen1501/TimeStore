using System.Text;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Primitives;

namespace TimeStore.Infra.Backups;

/// <param name="Archive">The archive to restore (<c>.tar.zst</c> or <c>.tar</c>, detected from its bytes): a file, or an
/// <c>http(s)://</c> URL read as it downloads (<see cref="ArchiveSource"/>).</param>
/// <param name="DataPath">The data directory to fill; it must be empty or missing.</param>
/// <param name="MaxSchemaVersion">The newest metadata schema this build knows.</param>
/// <param name="IfEmpty">A data directory that holds data is left alone (a success, nothing read): the restore of an
/// init container that runs at every start, but restores only into a new, empty volume.</param>
public sealed record RestoreRequest(string Archive, string DataPath, int MaxSchemaVersion, bool IfEmpty = false);

/// <param name="Manifest">What was restored; null when <see cref="RestoreRequest.IfEmpty"/> skipped a directory with data.</param>
/// <param name="MasterKeyRestored">Whether the archive held a master key (archives of versions before the key became
/// configuration): it is written to <c>master.key</c> in the data directory, for the operator to move into the
/// configuration.</param>
public sealed record RestoreSummary(BackupManifest? Manifest, string Compression, bool MasterKeyRestored)
{
    public static readonly RestoreSummary Skipped = new(null, string.Empty, false);

    public bool WasSkipped => Manifest is null;
}

/// <summary>
/// Restores an archive into an empty data directory with the server stopped (docs/research/backup.md section 7). Every
/// entry is checked before the restore counts as done: the manifest (format, schema), the metadata SHA-256, every
/// blob's SHA-256 against <c>blobs.sha256</c>, and nothing unlisted or missing. On any failure everything written is
/// removed again, so a failed restore never leaves a data directory a server would start on.
/// </summary>
/// <remarks>
/// While it writes, the data directory holds <see cref="InProgressMarker"/>, removed last. A process killed half way
/// (a pod evicted during an init container's restore) leaves it: the next restore wipes the directory and starts over
/// (everything in it came from that restore, which only starts in an empty directory), and the server refuses to start
/// on it.
/// </remarks>
public sealed class BackupRestorer(IBackupArchiveFormat format)
{
    /// <summary>Present in a data directory while a restore writes it (see remarks).</summary>
    public const string InProgressMarker = ".timestore-restore";

    private const string TempDirectory = "tmp";
    private const string ChecksumListName = "restore-blobs.sha256.tmp";
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // ext4 creates it at the root of every file system (Longhorn, EBS, Ceph RBD volumes): an empty volume still has it.
    private const string LostAndFound = "lost+found";

    public async ValueTask<Result<RestoreSummary>> RestoreAsync(RestoreRequest request, CancellationToken ct)
    {
        var data = new DirectoryInfo(Path.GetFullPath(request.DataPath));
        var interrupted = data.Exists && File.Exists(Path.Combine(data.FullName, InProgressMarker));
        if (!interrupted && data.Exists && data.EnumerateFileSystemInfos().Any(entry => entry.Name != LostAndFound))
            return request.IfEmpty ? RestoreSummary.Skipped : BackupErrors.RestoreNotEmpty;

        var createdDirectory = !data.Exists;
        data.Create();
        if (interrupted)
            Clean(data, createdDirectory: false, masterKeyPath: null); // an earlier restore of this directory was cut
        await File.WriteAllTextAsync(Path.Combine(data.FullName, InProgressMarker), string.Empty, ct);
        var masterKeyPath = Path.Combine(data.FullName, BackupLayout.MasterKeyEntry);
        var wroteMasterKey = false;
        try
        {
            var result = await UnpackAsync(request, data, masterKeyPath, () => wroteMasterKey = true, ct);
            if (result.IsFailure)
                Clean(data, createdDirectory, wroteMasterKey ? masterKeyPath : null);
            else
                File.Delete(Path.Combine(data.FullName, InProgressMarker)); // last: the directory is complete
            return result;
        }
        catch (InvalidDataException)
        {
            Clean(data, createdDirectory, wroteMasterKey ? masterKeyPath : null);
            return BackupArchiveOpener.Damaged;
        }
        catch
        {
            Clean(data, createdDirectory, wroteMasterKey ? masterKeyPath : null);
            throw;
        }
    }

    private async ValueTask<Result<RestoreSummary>> UnpackAsync(
        RestoreRequest request, DirectoryInfo data, string masterKeyPath, Action masterKeyWritten, CancellationToken ct)
    {
        // 1. The manifest comes first: refuse what this build cannot restore before writing anything.
        var opened = await BackupArchiveOpener.OpenAsync(format, request.Archive, Domain.Backups.BackupContents.Full,
            "it has no blobs: restore it with --metadata onto the data directory it came from", request.MaxSchemaVersion, ct);
        if (opened.IsFailure)
            return opened.Error;
        await using var archive = opened.Value;
        var (reader, manifest) = (archive.Reader, archive.Manifest);

        // 2. Entries, each hashed while it is written.
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var checksumList = Path.Combine(data.FullName, TempDirectory, ChecksumListName);
        var sawChecksums = false;
        long blobs = 0;
        while (await reader.NextAsync(ct) is { } entry)
        {
            if (sawChecksums)
                return BackupErrors.RestoreInvalidArchive($"entry '{entry.Name}' follows {BackupLayout.ChecksumsEntry}.");
            if (hashes.ContainsKey(entry.Name))
                return BackupErrors.RestoreInvalidArchive($"entry '{entry.Name}' appears twice.");

            if (entry.Name == BackupLayout.MetadataEntry)
            {
                var sha256 = await WriteAsync(entry, Path.Combine(data.FullName, BackupLayout.MetadataEntry), null, flush: true, ct);
                if (sha256 != manifest.MetadataSha256)
                    return BackupErrors.RestoreChecksumMismatch(entry.Name);
                hashes[entry.Name] = sha256;
            }
            else if (BackupLayout.TryParseBlobEntry(entry.Name, out _))
            {
                // The name was checked to be exactly blobs/<xy>/<32 hex>: it cannot leave the data directory.
                var path = Path.Combine(data.FullName, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                hashes[entry.Name] = await WriteAsync(entry, path, null, flush: false, ct);
                blobs++;
            }
            else if (entry.Name == BackupLayout.MasterKeyEntry && manifest.IncludesMasterKey)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(masterKeyPath)!);
                hashes[entry.Name] = await WriteAsync(entry, masterKeyPath, PrivateFile, flush: true, ct, created: masterKeyWritten);
            }
            else if (entry.Name == BackupLayout.ChecksumsEntry)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(checksumList)!);
                await WriteAsync(entry, checksumList, null, flush: false, ct);
                sawChecksums = true;
            }
            else
            {
                return BackupErrors.RestoreInvalidArchive($"unexpected entry '{entry.Name}'.");
            }
        }

        // 3. Everything the archive promised is there, and matches the checksum list exactly.
        if (!hashes.ContainsKey(BackupLayout.MetadataEntry))
            return BackupErrors.RestoreInvalidArchive($"{BackupLayout.MetadataEntry} is missing.");
        if (!sawChecksums)
            return BackupErrors.RestoreInvalidArchive($"{BackupLayout.ChecksumsEntry} is missing (the archive is incomplete).");
        if (blobs != manifest.Blobs)
            return BackupErrors.RestoreInvalidArchive($"it holds {blobs} blobs, the manifest lists {manifest.Blobs}.");
        if (manifest.IncludesMasterKey && !hashes.ContainsKey(BackupLayout.MasterKeyEntry))
            return BackupErrors.RestoreInvalidArchive($"{BackupLayout.MasterKeyEntry} is missing.");

        var verified = await VerifyAsync(checksumList, hashes, ct);
        File.Delete(checksumList);
        if (verified.IsFailure)
            return verified.Error;

        return new RestoreSummary(manifest, reader.Compression, manifest.IncludesMasterKey);
    }

    private static async ValueTask<Result> VerifyAsync(string checksumList, Dictionary<string, string> hashes, CancellationToken ct)
    {
        var listed = 0;
        using var lines = new StreamReader(checksumList, Encoding.ASCII);
        while (await lines.ReadLineAsync(ct) is { } line)
        {
            if (!BackupLayout.TryParseChecksumLine(line, out var sha256, out var entry))
                return BackupErrors.RestoreInvalidArchive($"{BackupLayout.ChecksumsEntry} has a malformed line.");
            if (!hashes.TryGetValue(entry, out var actual))
                return BackupErrors.RestoreInvalidArchive($"'{entry}' is listed in {BackupLayout.ChecksumsEntry} but missing.");
            if (actual != sha256)
                return BackupErrors.RestoreChecksumMismatch(entry);
            listed++;
        }

        return listed == hashes.Count
            ? Result.Success
            : BackupErrors.RestoreInvalidArchive($"some entries are not listed in {BackupLayout.ChecksumsEntry}.");
    }

    /// <param name="created">Called once the file exists (it is ours to remove from then on).</param>
    private static async ValueTask<string> WriteAsync(
        ArchiveEntry entry, string path, UnixFileMode? mode, bool flush, CancellationToken ct, Action? created = null)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (mode is { } unixMode && !OperatingSystem.IsWindows())
            options.UnixCreateMode = unixMode;

        await using var target = new FileStream(path, options);
        created?.Invoke();
        var hashes = await entry.CopyToAsync(target, ct);
        if (flush)
            target.Flush(flushToDisk: true);
        return hashes.Sha256Hex;
    }

    /// <summary>Removes what the restore wrote (the directory was empty before), and the master key it wrote.</summary>
    private static void Clean(DirectoryInfo data, bool createdDirectory, string? masterKeyPath)
    {
        if (masterKeyPath is not null)
            File.Delete(masterKeyPath);
        data.Refresh();
        if (!data.Exists)
            return;
        if (createdDirectory)
        {
            data.Delete(recursive: true);
            return;
        }

        foreach (var directory in data.EnumerateDirectories())
        {
            if (directory.Name != LostAndFound)
                directory.Delete(recursive: true);
        }
        foreach (var file in data.EnumerateFiles())
            file.Delete();
    }
}
