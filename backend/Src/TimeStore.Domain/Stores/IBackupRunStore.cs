using TimeStore.Domain.Backups;

namespace TimeStore.Domain.Stores;

/// <summary>
/// The <c>backup_runs</c> history: the source of truth for every backup. Implemented by the metadata provider (the
/// runs live in the metadata database). Every behaviour is pinned by <c>BackupRunStoreContract</c>.
/// </summary>
public interface IBackupRunStore
{
    /// <summary>Inserts a queued run; false when another run is queued or running (at most one active run).</summary>
    ValueTask<bool> TryCreateBackupRunAsync(BackupRun run, CancellationToken ct);

    ValueTask<BackupRun?> FindBackupRunAsync(Guid runId, CancellationToken ct);

    /// <summary>The newest runs first, at most <paramref name="limit"/>.</summary>
    ValueTask<IReadOnlyList<BackupRun>> ListBackupRunsAsync(int limit, CancellationToken ct);

    /// <summary>Queued runs, oldest first (the worker wakes them again after a restart).</summary>
    ValueTask<IReadOnlyList<Guid>> ListQueuedBackupRunIdsAsync(CancellationToken ct);

    /// <summary>True when a run already carries <paramref name="archiveName"/> (its archive may since be deleted).</summary>
    ValueTask<bool> IsBackupArchiveNameUsedAsync(string archiveName, CancellationToken ct);

    /// <summary>Queued → running with its archive name; false when the run is not queued (cancelled, taken).</summary>
    ValueTask<bool> TryStartBackupRunAsync(Guid runId, string archiveName, DateTime startedAt, CancellationToken ct);

    /// <summary>Records instant T and the progress of a running run (no-op when it is no longer running).</summary>
    ValueTask UpdateBackupProgressAsync(Guid runId, DateTime snapshotAt, BackupProgress progress, CancellationToken ct);

    /// <summary>Moves a queued or running run to its terminal status; false when it had already ended.</summary>
    ValueTask<bool> TryFinishBackupRunAsync(Guid runId, BackupOutcome outcome, CancellationToken ct);

    /// <summary>
    /// Startup: running runs become interrupted with the given error (the process that ran them is gone); returns how
    /// many. Single node: nothing runs before startup completes (several nodes: only runs whose lease expired,
    /// docs/roadmap.md).
    /// </summary>
    ValueTask<int> InterruptRunningBackupRunsAsync(DateTime now, string errorCode, string errorMessage, CancellationToken ct);
}
