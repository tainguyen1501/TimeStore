using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Domain.Backups;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Backups;

/// <summary>
/// The backup engine (docs/research/backup.md sections 3-4): claim the run, hold blob deletions, snapshot the metadata
/// (instant T), then write <c>manifest.json</c>, <c>metadata.db</c>, every referenced blob (each checked against its
/// stored MD5 and size), the master key when asked for, and <c>blobs.sha256</c>; commit the archive, finish the run.
/// Knows nothing about triggers, schedules or HTTP: it gets a run id and a target.
/// </summary>
public sealed class BackupRunner(
    IBackupRunStore runs,
    IMetadataSnapshots snapshots,
    IBlobStore blobs,
    IBlobHold hold,
    IBackupArchiveFormat format,
    IEnumerable<IBackupArchiveStore> stores,
    BackupOptions options,
    TimeProvider clock)
    : IBackupRunner
{
    private const string ChecksumListPrefix = "backup-";
    private const string ChecksumListExtension = ".sha256.tmp"; // *.tmp: removed at startup after a crash
    private const string IdFormat = "N";

    private static readonly string Version =
        typeof(BackupRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? string.Empty;

    // Runs executing in this process (single node: at most one), so a cancel request reaches them.
    private readonly ConcurrentDictionary<Guid, RunControl> _running = new();

    public async ValueTask<BackupExecution?> RunQueuedAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.FindBackupRunAsync(runId, ct);
        if (run is not { Status: BackupStatus.Queued })
            return null;

        if (stores.FirstOrDefault(store => store.Type == run.TargetType) is { } target)
            return await RunAsync(runId, target, ct);

        // The target is no longer configured (e.g. Backup:Path removed before a restart).
        await runs.TryFinishBackupRunAsync(runId, Ended(BackupStatus.Failed, BackupProgress.None, BackupErrors.TargetNotConfigured), ct);
        return await ExecutionAsync(runId, BackupErrors.TargetNotConfigured, null);
    }

    public async ValueTask<BackupExecution?> RunAsync(Guid runId, IBackupTarget target, CancellationToken ct)
    {
        var run = await runs.FindBackupRunAsync(runId, ct);
        if (run is not { Status: BackupStatus.Queued })
            return null;

        var startedAt = clock.UtcNowMs;
        var archiveName = BackupLayout.ArchiveName(options.NodeId, startedAt, format.ExtensionOf(run.Compression));
        if (!await runs.TryStartBackupRunAsync(runId, archiveName, startedAt, ct))
            return null;

        using var control = new RunControl(ct);
        _running[runId] = control;
        AppError? error = null;
        Exception? exception = null;
        ArchiveSummary? archive = null;
        try
        {
            archive = await WriteAsync(run, archiveName, target, control);
        }
        catch (BackupFailure failure)
        {
            error = failure.Error;
        }
        catch (OperationCanceledException) when (control.Token.IsCancellationRequested)
        {
            error = control.CancelledByUser ? BackupErrors.Cancelled : BackupErrors.Interrupted;
        }
        catch (Exception unexpected)
        {
            // A cancelled run whose target was writing (the client went away) surfaces as an I/O error, not a cancel.
            if (control.Token.IsCancellationRequested)
                error = control.CancelledByUser ? BackupErrors.Cancelled : BackupErrors.Interrupted;
            else
                (error, exception) = (BackupErrors.Failed, unexpected);
        }
        finally
        {
            _running.TryRemove(runId, out _);
        }

        var outcome = error is null
            ? new BackupOutcome(BackupStatus.Succeeded, clock.UtcNowMs, control.Progress, archive!.Value.Size, archive.Value.Sha256Hex, null, null)
            : Ended(StatusOf(error), control.Progress, error);
        // The run's own state is recorded even when the caller has gone (CancellationToken.None).
        await runs.TryFinishBackupRunAsync(runId, outcome, CancellationToken.None);
        return await ExecutionAsync(runId, error, exception);
    }

    public bool Cancel(Guid runId)
    {
        if (!_running.TryGetValue(runId, out var control))
            return false;
        control.CancelByUser();
        return true;
    }

    public async ValueTask<IReadOnlyList<Guid>> RecoverAsync(CancellationToken ct)
    {
        var now = clock.UtcNowMs;
        await runs.InterruptRunningBackupRunsAsync(now, BackupErrors.Interrupted.Code, BackupErrors.Interrupted.Message, ct);
        foreach (var store in stores)
            await store.DeleteIncompleteAsync(ct);
        var queued = new List<Guid>();
        foreach (var runId in await runs.ListQueuedBackupRunIdsAsync(ct))
        {
            var run = await runs.FindBackupRunAsync(runId, ct);
            if (run?.TargetType == BackupTargetTypes.Stream)
                await runs.TryFinishBackupRunAsync(runId, Ended(BackupStatus.Interrupted, BackupProgress.None, BackupErrors.Interrupted), ct);
            else if (run is not null)
                queued.Add(runId);
        }

        return queued;
    }

    private async ValueTask<ArchiveSummary> WriteAsync(BackupRun run, string archiveName, IBackupTarget target, RunControl control)
    {
        var ct = control.Token;
        EnsureFreeSpace();

        // Hold first, then snapshot: every blob the snapshot references stays on disk until the archive has it.
        await using var held = hold.Hold();
        await using var snapshot = await snapshots.CreateAsync(run.Id, ct);
        var snapshotAt = clock.UtcNowMs;
        var summary = snapshot.Summary;
        control.Progress = new BackupProgress(summary.Blobs, 0, summary.Bytes, 0);
        await runs.UpdateBackupProgressAsync(run.Id, snapshotAt, control.Progress, ct);

        string metadataSha256;
        await using (var database = snapshot.OpenRead())
            metadataSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(database, ct));

        var masterKey = run.IncludeMasterKey ? await File.ReadAllBytesAsync(options.MasterKeyPath, ct) : null;
        var manifest = format.SerializeManifest(new BackupManifest(
            BackupLayout.FormatVersion, Version, options.NodeId, snapshotAt, summary.SchemaVersion, run.Compression,
            summary.Buckets, summary.Objects, summary.Versions, summary.Blobs, summary.Bytes, summary.DatabaseBytes,
            metadataSha256, masterKey is not null));

        await using var checksums = OpenChecksumList(run.Id);
        await using var upload = await target.OpenWriteAsync(archiveName, ct);
        await using var writer = format.CreateWriter(upload.Stream, run.Compression, options.CompressionLevel);

        await writer.AddAsync(BackupLayout.ManifestEntry, new MemoryStream(manifest, writable: false), manifest.Length, ct);
        await using (var database = snapshot.OpenRead())
        {
            var hashes = await writer.AddAsync(BackupLayout.MetadataEntry, database, summary.DatabaseBytes, ct);
            if (hashes.Sha256Hex != metadataSha256)
                throw new InvalidOperationException("The metadata snapshot changed while it was archived.");
            await AppendChecksumAsync(checksums, hashes.Sha256Hex, BackupLayout.MetadataEntry, ct);
        }

        var nextReport = clock.GetUtcNow() + BackupOptions.ProgressInterval;
        long blobsDone = 0, bytesDone = 0;
        await foreach (var blob in snapshot.ListBlobsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var entry = BackupLayout.BlobEntry(blob.BlobId);
            var hashes = await CopyBlobAsync(writer, blob, entry, ct);
            await AppendChecksumAsync(checksums, hashes.Sha256Hex, entry, ct);
            blobsDone++;
            bytesDone += blob.Size;
            control.Progress = control.Progress with { BlobsDone = blobsDone, BytesDone = bytesDone };
            if (clock.GetUtcNow() >= nextReport)
            {
                await runs.UpdateBackupProgressAsync(run.Id, snapshotAt, control.Progress, ct);
                nextReport = clock.GetUtcNow() + BackupOptions.ProgressInterval;
            }
        }

        if (masterKey is not null)
        {
            var hashes = await writer.AddAsync(BackupLayout.MasterKeyEntry, new MemoryStream(masterKey, writable: false), masterKey.Length, ct);
            CryptographicOperations.ZeroMemory(masterKey);
            await AppendChecksumAsync(checksums, hashes.Sha256Hex, BackupLayout.MasterKeyEntry, ct);
        }

        await checksums.FlushAsync(ct);
        checksums.Position = 0;
        await writer.AddAsync(BackupLayout.ChecksumsEntry, checksums, checksums.Length, ct);
        var archive = await writer.CompleteAsync(ct);
        await upload.CommitAsync(ct);
        return archive;
    }

    /// <summary>Copies one blob, checking it against its locator: present, same size, same MD5.</summary>
    private async ValueTask<ArchiveEntryHashes> CopyBlobAsync(IBackupArchiveWriter writer, BlobLocator blob, string entry, CancellationToken ct)
    {
        Stream source;
        try
        {
            source = await blobs.OpenReadAsync(blob, ct);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BackupFailure(BackupErrors.BlobMissing(blob.BlobId));
        }

        await using (source)
        {
            if (source.CanSeek && source.Length != blob.Size)
                throw new BackupFailure(BackupErrors.BlobCorrupt(blob.BlobId));

            ArchiveEntryHashes hashes;
            try
            {
                hashes = await writer.AddAsync(entry, source, blob.Size, ct);
            }
            catch (InvalidDataException)
            {
                throw new BackupFailure(BackupErrors.BlobCorrupt(blob.BlobId)); // shorter than recorded
            }

            return string.Equals(hashes.Md5Hex, blob.Md5Hex, StringComparison.OrdinalIgnoreCase)
                ? hashes
                : throw new BackupFailure(BackupErrors.BlobCorrupt(blob.BlobId));
        }
    }

    private void EnsureFreeSpace()
    {
        if (options.MinFreeBytes <= 0)
            return;
        var free = new DriveInfo(Path.GetFullPath(options.DataPath)).AvailableFreeSpace;
        if (free < options.MinFreeBytes)
            throw new BackupFailure(BackupErrors.LowDiskSpace(free, options.MinFreeBytes));
    }

    /// <summary>The lines of <c>blobs.sha256</c>, kept on disk (millions of blobs) and deleted when closed.</summary>
    private FileStream OpenChecksumList(Guid runId)
    {
        Directory.CreateDirectory(options.TempPath);
        return new FileStream(
            Path.Combine(options.TempPath, ChecksumListPrefix + runId.ToString(IdFormat) + ChecksumListExtension),
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            });
    }

    private static ValueTask AppendChecksumAsync(FileStream checksums, string sha256Hex, string entry, CancellationToken ct) =>
        checksums.WriteAsync(Encoding.ASCII.GetBytes(BackupLayout.ChecksumLine(sha256Hex, entry)), ct);

    private async ValueTask<BackupExecution> ExecutionAsync(Guid runId, AppError? error, Exception? exception) =>
        new((await runs.FindBackupRunAsync(runId, CancellationToken.None))!, error, exception);

    private BackupOutcome Ended(BackupStatus status, BackupProgress progress, AppError error) =>
        new(status, clock.UtcNowMs, progress, null, null, error.Code, error.Message);

    private static BackupStatus StatusOf(AppError error) => error.Code switch
    {
        BackupErrors.Codes.Cancelled => BackupStatus.Cancelled,
        BackupErrors.Codes.Interrupted => BackupStatus.Interrupted,
        _ => BackupStatus.Failed,
    };

    /// <summary>A run's cancellation: the caller's token (shutdown, client gone) or a cancel request.</summary>
    private sealed class RunControl(CancellationToken outer) : IDisposable
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        private volatile bool _cancelledByUser;

        public CancellationToken Token => _source.Token;

        public bool CancelledByUser => _cancelledByUser;

        public BackupProgress Progress { get; set; } = BackupProgress.None;

        public void CancelByUser()
        {
            _cancelledByUser = true;
            try
            {
                _source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the run ended meanwhile
            }
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>A run failed for a reason the run records (not a bug).</summary>
    private sealed class BackupFailure(AppError error) : Exception(error.Message)
    {
        public AppError Error => error;
    }
}
