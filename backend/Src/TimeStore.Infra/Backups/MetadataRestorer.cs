using System.Globalization;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Domain.Backups;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Backups;

/// <param name="Archive">A metadata-only archive: a file or an <c>http(s)://</c> URL.</param>
/// <param name="DataPath">The data directory it came from (its <c>metadata.db</c> and <c>blobs/</c>), server stopped.</param>
/// <param name="Once">Skip when this archive was already restored here (the Helm chart's init container runs at every
/// start).</param>
/// <param name="AllowMissing">Restore although some referenced blobs are gone (those objects then fail to read).</param>
public sealed record MetadataRestoreRequest(
    string Archive, string DataPath, int MaxSchemaVersion, bool Once = false, bool AllowMissing = false);

/// <param name="Manifest">What was restored; null when <see cref="MetadataRestoreRequest.Once"/> skipped it.</param>
/// <param name="Missing">Referenced blobs not found (restored anyway with <c>AllowMissing</c>).</param>
/// <param name="PreviousCopy">Where the replaced metadata was moved (to go back by hand).</param>
public sealed record MetadataRestoreSummary(BackupManifest? Manifest, long Referenced, long Missing, string? PreviousCopy)
{
    public static readonly MetadataRestoreSummary Skipped = new(null, 0, 0, null);

    public bool WasSkipped => Manifest is null;
}

/// <summary>
/// Restores a metadata-only backup onto the data directory it came from (docs/research/blob-retention.md, B5b): the
/// server's metadata goes back to the backup's instant, and every blob it references must still be on disk, kept by
/// the blob retention. Order: read and check the archive into a temporary file; check every referenced blob (exists,
/// same size) and refuse, changing nothing, when some are gone; then swap the database in and keep the replaced one.
/// </summary>
/// <remarks>
/// The swap holds <see cref="InProgressMarker"/> (the server refuses to start while it is there); a restore cut half
/// way is simply run again. <see cref="AppliedFile"/> records the last restored archive (its metadata SHA-256) for
/// <see cref="MetadataRestoreRequest.Once"/>. Nothing else needs fixing afterwards: blobs written after the backup are
/// orphans for the sweep, blobs released after it are referenced again, and the restored database's own list of
/// released blobs goes on.
/// </remarks>
public sealed class MetadataRestorer(IBackupArchiveFormat format, IMetadataFiles files, IBlobStore blobs)
{
    public const string InProgressMarker = ".timestore-restore-metadata";
    public const string AppliedFile = ".timestore-restored-metadata";
    public const string PreviousPrefix = "metadata-before-restore-";

    private const string TempDirectory = "tmp";
    private const string TempName = "restore-metadata.db.tmp";
    private const string ChecksumListName = "restore-metadata.sha256.tmp";
    private const string WalSuffix = "-wal";
    private const string ShmSuffix = "-shm";
    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string SequenceSeparator = "-";
    private const int MaxExamples = 5;
    private const string NodeAny = "";

    public async ValueTask<Result<MetadataRestoreSummary>> RestoreAsync(
        MetadataRestoreRequest request, DateTime now, CancellationToken ct)
    {
        var data = Path.GetFullPath(request.DataPath);
        var database = Path.Combine(data, BackupLayout.MetadataEntry);
        var interrupted = File.Exists(Path.Combine(data, InProgressMarker));
        if (!interrupted && (!File.Exists(database) || !Directory.Exists(Path.Combine(data, BackupLayout.BlobsDirectory))))
            return BackupErrors.RestoreNoData;

        var temp = Path.Combine(data, TempDirectory, TempName);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            var extracted = await ExtractAsync(request, data, temp, ct);
            if (extracted.IsFailure)
                return extracted.Error;
            if (extracted.Value is not { } manifest)
                return MetadataRestoreSummary.Skipped;

            // Every blob the restored metadata references must be here (before anything changes).
            long referenced = 0, missing = 0;
            var examples = new List<string>(MaxExamples);
            await foreach (var blob in files.ListReferencedBlobsAsync(temp, ct))
            {
                referenced++;
                if (await HasAsync(blob, ct))
                    continue;
                missing++;
                if (examples.Count < MaxExamples)
                    examples.Add($"{blob.Bucket}/{blob.Key}");
            }

            if (missing > 0 && !request.AllowMissing)
                return BackupErrors.RestoreMissingBlobs(missing, referenced, examples);

            var previous = await SwapAsync(data, database, temp, manifest, now, ct);
            return new MetadataRestoreSummary(manifest, referenced, missing, previous);
        }
        catch (InvalidDataException)
        {
            return BackupArchiveOpener.Damaged;
        }
        finally
        {
            File.Delete(temp);
            File.Delete(Path.Combine(data, TempDirectory, ChecksumListName));
        }
    }

    /// <summary>The checked metadata in <paramref name="temp"/>, or null when <c>Once</c> found it already restored.</summary>
    private async ValueTask<Result<BackupManifest?>> ExtractAsync(
        MetadataRestoreRequest request, string data, string temp, CancellationToken ct)
    {
        var opened = await BackupArchiveOpener.OpenAsync(format, request.Archive, BackupContents.Metadata,
            "restore it into an empty data directory without --metadata", request.MaxSchemaVersion, ct);
        if (opened.IsFailure)
            return opened.Error;
        await using var archive = opened.Value;
        var (reader, manifest) = (archive.Reader, archive.Manifest);
        if (request.Once && await AppliedAsync(data, ct) == manifest.MetadataSha256)
            return Result<BackupManifest?>.Ok(null);

        string? metadataSha256 = null, listedSha256 = null;
        while (await reader.NextAsync(ct) is { } entry)
        {
            if (entry.Name == BackupLayout.MetadataEntry && metadataSha256 is null)
            {
                await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                metadataSha256 = (await entry.CopyToAsync(target, ct)).Sha256Hex;
                target.Flush(flushToDisk: true);
            }
            else if (entry.Name == BackupLayout.ChecksumsEntry && listedSha256 is null)
            {
                using var list = new MemoryStream();
                await entry.CopyToAsync(list, ct);
                foreach (var line in System.Text.Encoding.ASCII.GetString(list.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (BackupLayout.TryParseChecksumLine(line, out var sha256, out var name) && name == BackupLayout.MetadataEntry)
                        listedSha256 = sha256;
                }
            }
            else
            {
                return BackupErrors.RestoreInvalidArchive($"unexpected entry '{entry.Name}' in a metadata-only archive.");
            }
        }

        if (metadataSha256 is null)
            return BackupErrors.RestoreInvalidArchive($"{BackupLayout.MetadataEntry} is missing.");
        if (listedSha256 is null)
            return BackupErrors.RestoreInvalidArchive($"{BackupLayout.ChecksumsEntry} is missing (the archive is incomplete).");
        if (metadataSha256 != manifest.MetadataSha256 || metadataSha256 != listedSha256)
            return BackupErrors.RestoreChecksumMismatch(BackupLayout.MetadataEntry);
        return manifest;
    }

    private async ValueTask<bool> HasAsync(ReferencedBlob blob, CancellationToken ct)
    {
        try
        {
            await using var stream = await blobs.OpenReadAsync(new BlobLocator(blob.BlobId, NodeAny, blob.Size, string.Empty), ct);
            return !stream.CanSeek || stream.Length == blob.Size;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the current database (with its WAL and shared memory) aside, then the restored one in. Repeated after a
    /// cut, it moves only what is still there.
    /// </summary>
    private static async ValueTask<string> SwapAsync(
        string data, string database, string temp, BackupManifest manifest, DateTime now, CancellationToken ct)
    {
        var marker = Path.Combine(data, InProgressMarker);
        await File.WriteAllTextAsync(marker, string.Empty, ct);

        // A folder of its own, even for two restores of the same second (a metadata restore takes milliseconds).
        var stamp = PreviousPrefix + now.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var previous = Path.Combine(data, stamp);
        for (var sequence = 2; Directory.Exists(previous); sequence++)
            previous = Path.Combine(data, stamp + SequenceSeparator + sequence.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(previous);
        foreach (var file in new[] { database, database + WalSuffix, database + ShmSuffix })
        {
            if (File.Exists(file))
                File.Move(file, Path.Combine(previous, Path.GetFileName(file)));
        }

        File.Move(temp, database);
        await File.WriteAllTextAsync(Path.Combine(data, AppliedFile), manifest.MetadataSha256, ct);
        File.Delete(marker); // last: the data directory is consistent again
        return previous;
    }

    private static async ValueTask<string?> AppliedAsync(string data, CancellationToken ct)
    {
        var path = Path.Combine(data, AppliedFile);
        return File.Exists(path) ? (await File.ReadAllTextAsync(path, ct)).Trim() : null;
    }
}
