using TimeStore.Contracts.Backups;
using TimeStore.Domain.Backups;

namespace TimeStore.Application.Features.Backups;

public static class BackupMappings
{
    extension(BackupRun run)
    {
        public BackupRunResponse ToResponse() => new(
            run.Id, StatusOf(run.Status), run.Trigger, run.TargetType, run.Compression, run.StartedBy,
            run.QueuedAt, run.StartedAt, run.FinishedAt, run.SnapshotAt, run.Progress.BlobsTotal, run.Progress.BlobsDone,
            run.Progress.BytesTotal, run.Progress.BytesDone, run.ArchiveName, run.ArchiveSize, run.ArchiveSha256,
            run.ErrorCode, run.ErrorMessage, run.Content);
    }

    private static string StatusOf(BackupStatus status) => status switch
    {
        BackupStatus.Queued => BackupRunStatuses.Queued,
        BackupStatus.Running => BackupRunStatuses.Running,
        BackupStatus.Succeeded => BackupRunStatuses.Succeeded,
        BackupStatus.Failed => BackupRunStatuses.Failed,
        BackupStatus.Cancelled => BackupRunStatuses.Cancelled,
        BackupStatus.Interrupted => BackupRunStatuses.Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
