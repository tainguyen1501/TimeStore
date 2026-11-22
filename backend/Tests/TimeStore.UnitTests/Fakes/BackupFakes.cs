using System.Runtime.CompilerServices;
using TimeStore.Application.Abstractions;
using TimeStore.Domain.Backups;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;

namespace TimeStore.UnitTests.Fakes;

/// <summary>
/// <see cref="IBackupRunStore"/> in memory with the contract's rules (one active run, transitions only from the right
/// status). The real provider is pinned by BackupStoreContract.
/// </summary>
internal sealed class InMemoryBackupRunStore : IBackupRunStore
{
    private readonly Dictionary<Guid, BackupRun> _runs = [];

    public ValueTask<bool> TryCreateBackupRunAsync(BackupRun run, CancellationToken ct)
    {
        lock (_runs)
        {
            if (_runs.Values.Any(r => r.IsActive))
                return ValueTask.FromResult(false);
            _runs[run.Id] = run;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<BackupRun?> FindBackupRunAsync(Guid runId, CancellationToken ct)
    {
        lock (_runs)
            return ValueTask.FromResult(_runs.GetValueOrDefault(runId));
    }

    public ValueTask<IReadOnlyList<BackupRun>> ListBackupRunsAsync(int limit, CancellationToken ct)
    {
        lock (_runs)
            return ValueTask.FromResult<IReadOnlyList<BackupRun>>(_runs.Values.OrderByDescending(r => r.Id).Take(limit).ToArray());
    }

    public ValueTask<bool> IsBackupArchiveNameUsedAsync(string archiveName, CancellationToken ct)
    {
        lock (_runs)
            return ValueTask.FromResult(_runs.Values.Any(r => r.ArchiveName == archiveName));
    }

    public ValueTask<IReadOnlyList<Guid>> ListQueuedBackupRunIdsAsync(CancellationToken ct)
    {
        lock (_runs)
            return ValueTask.FromResult<IReadOnlyList<Guid>>(
                _runs.Values.Where(r => r.Status == BackupStatus.Queued).OrderBy(r => r.Id).Select(r => r.Id).ToArray());
    }

    public ValueTask<bool> TryStartBackupRunAsync(Guid runId, string archiveName, DateTime startedAt, CancellationToken ct) =>
        Update(runId, r => r.Status == BackupStatus.Queued,
            r => r with { Status = BackupStatus.Running, ArchiveName = archiveName, StartedAt = startedAt });

    public async ValueTask UpdateBackupProgressAsync(Guid runId, DateTime snapshotAt, BackupProgress progress, CancellationToken ct)
    {
        ProgressUpdates++;
        await Update(runId, r => r.Status == BackupStatus.Running, r => r with { SnapshotAt = snapshotAt, Progress = progress });
    }

    public ValueTask<bool> TryFinishBackupRunAsync(Guid runId, BackupOutcome outcome, CancellationToken ct) =>
        Update(runId, r => r.IsActive, r => r with
        {
            Status = outcome.Status,
            FinishedAt = outcome.FinishedAt,
            Progress = outcome.Progress,
            ArchiveSize = outcome.ArchiveSize,
            ArchiveSha256 = outcome.ArchiveSha256,
            ErrorCode = outcome.ErrorCode,
            ErrorMessage = outcome.ErrorMessage,
        });

    public ValueTask<int> InterruptRunningBackupRunsAsync(DateTime now, string errorCode, string errorMessage, CancellationToken ct)
    {
        lock (_runs)
        {
            var running = _runs.Values.Where(r => r.Status == BackupStatus.Running).ToArray();
            foreach (var run in running)
                _runs[run.Id] = run with { Status = BackupStatus.Interrupted, FinishedAt = now, ErrorCode = errorCode, ErrorMessage = errorMessage };
            return ValueTask.FromResult(running.Length);
        }
    }

    public int ProgressUpdates { get; private set; }

    /// <summary>Puts a run in any state (startup scenarios).</summary>
    public void Seed(BackupRun run)
    {
        lock (_runs)
            _runs[run.Id] = run;
    }

    private ValueTask<bool> Update(Guid runId, Func<BackupRun, bool> when, Func<BackupRun, BackupRun> change)
    {
        lock (_runs)
        {
            if (!_runs.TryGetValue(runId, out var run) || !when(run))
                return ValueTask.FromResult(false);
            _runs[runId] = change(run);
            return ValueTask.FromResult(true);
        }
    }
}

/// <summary>Snapshots of a fixed list of blobs with a small fake database file.</summary>
internal sealed class FakeMetadataSnapshots(IReadOnlyList<BlobLocator> blobs) : IMetadataSnapshots
{
    public static readonly byte[] Database = "SQLite format 3\0 fake snapshot"u8.ToArray();

    /// <summary>Called when a snapshot is taken (the test checks the hold is active by then).</summary>
    public Action? OnCreate { get; init; }

    /// <summary>Called before each blob is handed out (a test cancels or damages something here).</summary>
    public Action<int>? OnBlob { get; init; }

    public int Created { get; private set; }

    public int Open { get; private set; }

    public ValueTask<IMetadataSnapshot> CreateAsync(Guid runId, CancellationToken ct)
    {
        OnCreate?.Invoke();
        Created++;
        Open++;
        var summary = new MetadataSnapshotSummary(10, 1, blobs.Count, blobs.Count, blobs.Count, blobs.Sum(b => b.Size), Database.Length);
        return ValueTask.FromResult<IMetadataSnapshot>(new Snapshot(this, blobs, summary));
    }

    private sealed class Snapshot(FakeMetadataSnapshots owner, IReadOnlyList<BlobLocator> blobs, MetadataSnapshotSummary summary)
        : IMetadataSnapshot
    {
        public MetadataSnapshotSummary Summary => summary;

        public async IAsyncEnumerable<BlobLocator> ListBlobsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < blobs.Count; i++)
            {
                owner.OnBlob?.Invoke(i);
                await Task.Yield();
                yield return blobs[i];
            }
        }

        public Stream OpenRead() => new MemoryStream(Database, writable: false);

        public ValueTask DisposeAsync()
        {
            owner.Open--;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A stored target in memory: what was committed, and whether an upload was abandoned.</summary>
internal sealed class MemoryBackupTarget(string type = BackupTargetTypes.Directory) : IBackupArchiveStore
{
    public Dictionary<string, byte[]> Archives { get; } = new(StringComparer.Ordinal);

    public int Abandoned { get; private set; }

    public string Type => type;

    public ValueTask<IBackupUpload> OpenWriteAsync(string archiveName, CancellationToken ct) =>
        ValueTask.FromResult<IBackupUpload>(new Upload(this, archiveName));

    /// <summary>Free space reported by <see cref="AvailableBytes"/>; null: not measurable.</summary>
    public long? FreeBytes { get; set; }

    public long? AvailableBytes() => FreeBytes;

    private readonly Dictionary<string, long> _committed = new(StringComparer.Ordinal);

    // Newest first, as the directory lists them (by modification time): committed archives in commit order, after the
    // ones a test put in Archives directly (older, by name). Names alone do not sort by time: "…Z-2.tar.zst" < "…Z.tar.zst".
    public ValueTask<IReadOnlyList<StoredBackupArchive>> ListAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<StoredBackupArchive>>(
            Archives.OrderByDescending(a => _committed.GetValueOrDefault(a.Key, -1))
                .ThenByDescending(a => a.Key, StringComparer.Ordinal)
                .Select(a => new StoredBackupArchive(a.Key, a.Value.Length, DateTime.UnixEpoch)).ToArray());

    public ValueTask<Stream?> OpenReadAsync(string archiveName, CancellationToken ct) =>
        ValueTask.FromResult<Stream?>(Archives.TryGetValue(archiveName, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    public ValueTask<bool> DeleteAsync(string archiveName, CancellationToken ct) => ValueTask.FromResult(Archives.Remove(archiveName));

    public int IncompleteCleanups { get; private set; }

    public ValueTask<int> DeleteIncompleteAsync(CancellationToken ct)
    {
        IncompleteCleanups++;
        return ValueTask.FromResult(0);
    }

    private sealed class Upload(MemoryBackupTarget owner, string name) : IBackupUpload
    {
        private readonly MemoryStream _stream = new();
        private bool _committed;

        public Stream Stream => _stream;

        public ValueTask CommitAsync(CancellationToken ct)
        {
            // As the directory target: a commit never replaces an archive.
            if (!owner.Archives.TryAdd(name, _stream.ToArray()))
                throw new IOException($"The archive '{name}' already exists.");
            owner._committed[name] = owner._committed.Count;
            _committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
                owner.Abandoned++;
            return ValueTask.CompletedTask;
        }
    }
}
