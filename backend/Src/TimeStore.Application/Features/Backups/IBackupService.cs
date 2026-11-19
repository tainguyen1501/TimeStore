using TimeStore.Application.Abstractions;
using TimeStore.Contracts.Backups;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Backups;

/// <summary>
/// Backups as the admin API sees them. Every way of starting one creates a <c>backup_runs</c> row first; a queued run
/// is executed by the backup worker, a streamed one by the request itself (docs/research/backup.md section 4).
/// </summary>
public interface IBackupService
{
    /// <summary>Queues a backup to a stored target; <c>BACKUP_ACTIVE</c> when one is already queued or running.</summary>
    ValueTask<Result<BackupRunResponse>> StartAsync(StartBackupRequest request, BackupCaller caller, CancellationToken ct);

    /// <summary>
    /// Runs a backup now, written to <paramref name="target"/> (the HTTP response). Errors before the first byte come
    /// back as a result; a failure after it ends the run as failed and returns its error too (the caller aborts the
    /// response so the client never takes a truncated archive for a complete one). An unexpected failure's exception
    /// comes along for the log.
    /// </summary>
    ValueTask<(Result<BackupRunResponse> Result, Exception? Exception)> StreamAsync(
        string? compression, BackupCaller caller, IBackupTarget target, CancellationToken ct);

    /// <summary>The newest runs first.</summary>
    ValueTask<IReadOnlyList<BackupRunResponse>> ListAsync(CancellationToken ct);

    ValueTask<Result<BackupRunResponse>> GetAsync(Guid runId, CancellationToken ct);

    /// <summary>A queued run is cancelled at once; a running one at its next blob.</summary>
    ValueTask<Result<BackupRunResponse>> CancelAsync(Guid runId, CancellationToken ct);

    /// <summary>The archive of a succeeded run kept by a stored target (the caller disposes the stream).</summary>
    ValueTask<Result<BackupArchiveDownload>> OpenArchiveAsync(Guid runId, CancellationToken ct);
}

public sealed record BackupArchiveDownload(string Name, string Compression, Stream Content);

/// <summary>Who starts a backup (an admin key or a key with the backup role).</summary>
public sealed record BackupCaller(string AccessKeyId);

/// <summary>Executes runs (the engine): claim, hold, snapshot, archive, finish. Used by the worker and the service.</summary>
public interface IBackupRunner
{
    /// <summary>
    /// Runs a queued run into <paramref name="target"/> and returns how it ended, or null when it was no longer queued
    /// (cancelled, or taken by someone else). Cancelling <paramref name="ct"/> ends the run as interrupted.
    /// </summary>
    ValueTask<BackupExecution?> RunAsync(Guid runId, IBackupTarget target, CancellationToken ct);

    /// <summary>Runs a queued run into the stored target it names (the worker).</summary>
    ValueTask<BackupExecution?> RunQueuedAsync(Guid runId, CancellationToken ct);

    /// <summary>Signals a running run of this process to stop; false when none runs under that id here.</summary>
    bool Cancel(Guid runId);

    /// <summary>
    /// Startup: running runs become interrupted (never re-run), queued streamed runs too (their request is gone), and
    /// stored targets drop the incomplete archives a crash left. Returns the queued runs to wake again, oldest first.
    /// </summary>
    ValueTask<IReadOnlyList<Guid>> RecoverAsync(CancellationToken ct);
}

/// <summary>How a run ended.</summary>
/// <param name="Error">Why it did not succeed (null on success).</param>
/// <param name="Exception">An unexpected failure, for the log (its message never reaches the run or a client).</param>
public sealed record BackupExecution(Domain.Backups.BackupRun Run, AppError? Error, Exception? Exception);

/// <summary>Wakes the backup worker. The row is the truth; the queue only carries ids (in memory).</summary>
public interface IBackupQueue
{
    void Enqueue(Guid runId);

    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}

/// <param name="Compression">Default compression (<c>zstd</c> or <c>none</c>).</param>
/// <param name="CompressionLevel">Zstandard level, 1-19.</param>
/// <param name="MinFreeBytes">A run refuses to start with less free space in the data directory (released blobs stay
/// on disk until it ends).</param>
/// <param name="DataPath">The data directory (free space is measured there).</param>
/// <param name="TempPath">Scratch space of a run (its checksum list), emptied of <c>*.tmp</c> files at startup.</param>
public sealed record BackupOptions(
    string NodeId,
    string DataPath,
    string TempPath,
    string Compression,
    int CompressionLevel,
    long MinFreeBytes)
{
    public const int DefaultCompressionLevel = 3;
    public const int MinCompressionLevel = 1;
    public const int MaxCompressionLevel = 19;
    public const long DefaultMinFreeBytes = 1L << 30;

    /// <summary>How often a running run writes its progress to its row.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    /// <summary>Runs listed by the admin API at most.</summary>
    public const int ListLimit = 100;
}
