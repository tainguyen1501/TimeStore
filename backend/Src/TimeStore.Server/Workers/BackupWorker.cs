using TimeStore.Application.Features.Backups;
using TimeStore.Domain.Backups;

namespace TimeStore.Server.Workers;

/// <summary>
/// Runs queued backups one at a time. Before the server serves, runs left running by a previous process become
/// interrupted (never re-run) and queued ones are woken again, oldest first (docs/research/backup.md section 4).
/// </summary>
internal sealed class BackupWorker(IBackupRunner runner, IBackupQueue queue, ILogger<BackupWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var runId in await runner.RecoverAsync(cancellationToken))
            queue.Enqueue(runId);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (await runner.RunQueuedAsync(runId, stoppingToken) is { } execution)
                        BackupLog.Ended(logger, execution);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    BackupLog.WorkerFailed(logger, runId, exception); // the run stays as recorded; the next one runs
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}

/// <summary>Every backup is logged: who started it, where it went, how it ended (never data or keys).</summary>
internal static partial class BackupLog
{
    public static void Ended(ILogger logger, BackupExecution execution)
    {
        var run = execution.Run;
        if (run.Status == BackupStatus.Succeeded)
            Succeeded(logger, run.Id, run.TargetType, run.ArchiveName, run.StartedBy, run.Progress.BlobsDone, run.ArchiveSize ?? 0);
        else
            NotSucceeded(logger, execution.Exception, run.Id, run.Status, run.TargetType, run.StartedBy, run.ErrorCode);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Backup {RunId} to {Target} succeeded: {ArchiveName} ({Blobs} blobs, {ArchiveBytes} bytes), started by {StartedBy}")]
    private static partial void Succeeded(
        ILogger logger, Guid runId, string target, string? archiveName, string startedBy, long blobs, long archiveBytes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Backup {RunId} ended {Status} ({Target}, started by {StartedBy}): {ErrorCode}")]
    private static partial void NotSucceeded(
        ILogger logger, Exception? exception, Guid runId, BackupStatus status, string target, string startedBy, string? errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup worker failed on run {RunId}")]
    public static partial void WorkerFailed(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Backup {RunId} streamed to {AccessKeyId}: {ArchiveName} ({ArchiveBytes} bytes)")]
    public static partial void Streamed(ILogger logger, Guid runId, string? archiveName, string accessKeyId, long archiveBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Streamed backup for {AccessKeyId} did not complete: {ErrorCode}")]
    public static partial void StreamEnded(ILogger logger, Exception? exception, string accessKeyId, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {RunId} cancelled by {AccessKeyId}")]
    public static partial void CancelRequested(ILogger logger, Guid runId, string accessKeyId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup archive of run {RunId} ({ArchiveName}) downloaded by {AccessKeyId}")]
    public static partial void Downloaded(ILogger logger, Guid runId, string archiveName, string accessKeyId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup archive of run {RunId} deleted by {AccessKeyId}")]
    public static partial void ArchiveDeleted(ILogger logger, Guid runId, string accessKeyId);
}
