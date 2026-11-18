using Microsoft.Data.Sqlite;
using TimeStore.Domain.Backups;
using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore : IBackupRunStore
{
    private const int SqliteConstraint = 19; // SQLITE_CONSTRAINT: the one-active-run index

    public async ValueTask<bool> TryCreateBackupRunAsync(BackupRun run, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BackupSql.Insert;
            command.Parameters.AddId(BackupSql.Id, run.Id);
            command.Parameters.AddText(BackupSql.Status, BackupStatusText.Format(run.Status));
            command.Parameters.AddText(BackupSql.Trigger, run.Trigger);
            command.Parameters.AddText(BackupSql.TargetType, run.TargetType);
            command.Parameters.AddText(BackupSql.Compression, run.Compression);
            command.Parameters.AddInt64(BackupSql.IncludeMasterKey, run.IncludeMasterKey ? 1 : 0);
            command.Parameters.AddText(BackupSql.StartedBy, run.StartedBy);
            command.Parameters.AddInt64(BackupSql.QueuedAt, SqliteTime.ToUnixMs(run.QueuedAt));
            try
            {
                return await command.ExecuteNonQueryAsync(ct) == 1;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteConstraint)
            {
                return false; // another run is active (ux_backup_runs_active)
            }
        });
    }

    public async ValueTask<BackupRun?> FindBackupRunAsync(Guid runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = BackupSql.Find;
        command.Parameters.AddId(BackupSql.Id, runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadBackupRun(reader) : null;
    }

    public async ValueTask<IReadOnlyList<BackupRun>> ListBackupRunsAsync(int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = BackupSql.ListNewest;
        command.Parameters.AddInt64(BackupSql.Limit, limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var runs = new List<BackupRun>();
        while (await reader.ReadAsync(ct))
            runs.Add(ReadBackupRun(reader));
        return runs;
    }

    public async ValueTask<IReadOnlyList<Guid>> ListQueuedBackupRunIdsAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = BackupSql.ListQueued;
        command.Parameters.AddText(BackupSql.Status, BackupStatusText.Queued);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetId(0));
        return ids;
    }

    public async ValueTask<bool> TryStartBackupRunAsync(Guid runId, string archiveName, DateTime startedAt, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BackupSql.Start;
            command.Parameters.AddId(BackupSql.Id, runId);
            command.Parameters.AddText(BackupSql.ArchiveName, archiveName);
            command.Parameters.AddInt64(BackupSql.StartedAt, SqliteTime.ToUnixMs(startedAt));
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask UpdateBackupProgressAsync(Guid runId, DateTime snapshotAt, BackupProgress progress, CancellationToken ct)
    {
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BackupSql.Progress;
            command.Parameters.AddId(BackupSql.Id, runId);
            command.Parameters.AddInt64(BackupSql.SnapshotAt, SqliteTime.ToUnixMs(snapshotAt));
            AddProgress(command.Parameters, progress);
            return await command.ExecuteNonQueryAsync(ct);
        });
    }

    public async ValueTask<bool> TryFinishBackupRunAsync(Guid runId, BackupOutcome outcome, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BackupSql.Finish;
            command.Parameters.AddId(BackupSql.Id, runId);
            command.Parameters.AddText(BackupSql.Status, BackupStatusText.Format(outcome.Status));
            command.Parameters.AddInt64(BackupSql.FinishedAt, SqliteTime.ToUnixMs(outcome.FinishedAt));
            AddProgress(command.Parameters, outcome.Progress);
            command.Parameters.AddNullableInt64(BackupSql.ArchiveSize, outcome.ArchiveSize);
            command.Parameters.AddNullableText(BackupSql.ArchiveSha256, outcome.ArchiveSha256);
            command.Parameters.AddNullableText(BackupSql.ErrorCode, outcome.ErrorCode);
            command.Parameters.AddNullableText(BackupSql.ErrorMessage, Truncate(outcome.ErrorMessage, BackupRun.ErrorMessageMaxLength));
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<int> InterruptRunningBackupRunsAsync(
        DateTime now, string errorCode, string errorMessage, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BackupSql.Interrupt;
            command.Parameters.AddInt64(BackupSql.FinishedAt, SqliteTime.ToUnixMs(now));
            command.Parameters.AddText(BackupSql.ErrorCode, errorCode);
            command.Parameters.AddText(BackupSql.ErrorMessage, Truncate(errorMessage, BackupRun.ErrorMessageMaxLength)!);
            return await command.ExecuteNonQueryAsync(ct);
        });
    }

    private static void AddProgress(SqliteParameterCollection parameters, BackupProgress progress)
    {
        parameters.AddInt64(BackupSql.BlobsTotal, progress.BlobsTotal);
        parameters.AddInt64(BackupSql.BlobsDone, progress.BlobsDone);
        parameters.AddInt64(BackupSql.BytesTotal, progress.BytesTotal);
        parameters.AddInt64(BackupSql.BytesDone, progress.BytesDone);
    }

    private static string? Truncate(string? text, int maxLength) =>
        text is null || text.Length <= maxLength ? text : text[..maxLength];

    // Column order of BackupSql.Columns.
    private static BackupRun ReadBackupRun(SqliteDataReader reader) => new(
        reader.GetId(0),
        BackupStatusText.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5) != 0,
        reader.GetString(6),
        SqliteTime.FromUnixMs(reader.GetInt64(7)),
        NullableTime(reader, 8),
        NullableTime(reader, 9),
        NullableTime(reader, 10),
        new BackupProgress(reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14)),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetInt64(16),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : reader.GetString(19));

    private static DateTime? NullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : SqliteTime.FromUnixMs(reader.GetInt64(ordinal));

    private static class BackupSql
    {
        public const string Id = "$id";
        public const string Status = "$status";
        public const string Trigger = "$trigger";
        public const string TargetType = "$targetType";
        public const string Compression = "$compression";
        public const string IncludeMasterKey = "$includeMasterKey";
        public const string StartedBy = "$startedBy";
        public const string QueuedAt = "$queuedAt";
        public const string StartedAt = "$startedAt";
        public const string FinishedAt = "$finishedAt";
        public const string SnapshotAt = "$snapshotAt";
        public const string BlobsTotal = "$blobsTotal";
        public const string BlobsDone = "$blobsDone";
        public const string BytesTotal = "$bytesTotal";
        public const string BytesDone = "$bytesDone";
        public const string ArchiveName = "$archiveName";
        public const string ArchiveSize = "$archiveSize";
        public const string ArchiveSha256 = "$archiveSha256";
        public const string ErrorCode = "$errorCode";
        public const string ErrorMessage = "$errorMessage";
        public const string Limit = "$limit";

        private const string Columns = """
            id, status, trigger_type, target_type, compression, include_master_key, started_by, queued_at, started_at,
            finished_at, snapshot_at, blobs_total, blobs_done, bytes_total, bytes_done, archive_name, archive_size,
            archive_sha256, error_code, error_message
            """;

        public const string Insert = """
            INSERT INTO backup_runs (id, status, trigger_type, target_type, compression, include_master_key, started_by,
                queued_at, blobs_total, blobs_done, bytes_total, bytes_done)
            VALUES ($id, $status, $trigger, $targetType, $compression, $includeMasterKey, $startedBy, $queuedAt, 0, 0, 0, 0);
            """;

        public const string Find = "SELECT " + Columns + " FROM backup_runs WHERE id = $id;";

        public const string ListNewest = "SELECT " + Columns + " FROM backup_runs ORDER BY id DESC LIMIT $limit;";

        public const string ListQueued = "SELECT id FROM backup_runs WHERE status = $status ORDER BY id;";

        public const string Start = """
            UPDATE backup_runs SET status = 'running', archive_name = $archiveName, started_at = $startedAt
            WHERE id = $id AND status = 'queued';
            """;

        public const string Progress = """
            UPDATE backup_runs SET snapshot_at = $snapshotAt, blobs_total = $blobsTotal, blobs_done = $blobsDone,
                bytes_total = $bytesTotal, bytes_done = $bytesDone
            WHERE id = $id AND status = 'running';
            """;

        public const string Finish = """
            UPDATE backup_runs SET status = $status, finished_at = $finishedAt, blobs_total = $blobsTotal,
                blobs_done = $blobsDone, bytes_total = $bytesTotal, bytes_done = $bytesDone, archive_size = $archiveSize,
                archive_sha256 = $archiveSha256, error_code = $errorCode, error_message = $errorMessage
            WHERE id = $id AND status IN ('queued', 'running');
            """;

        public const string Interrupt = """
            UPDATE backup_runs SET status = 'interrupted', finished_at = $finishedAt, error_code = $errorCode,
                error_message = $errorMessage
            WHERE status = 'running';
            """;
    }
}

/// <summary><c>backup_runs.status</c>: the lowercase status names (the migration's CHECK lists the same words).</summary>
internal static class BackupStatusText
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";

    public static BackupStatus Parse(string text) => text switch
    {
        Queued => BackupStatus.Queued,
        Running => BackupStatus.Running,
        Succeeded => BackupStatus.Succeeded,
        Failed => BackupStatus.Failed,
        Cancelled => BackupStatus.Cancelled,
        Interrupted => BackupStatus.Interrupted,
        _ => throw new InvalidDataException($"Unknown backup status '{text}'."),
    };

    public static string Format(BackupStatus status) => status switch
    {
        BackupStatus.Queued => Queued,
        BackupStatus.Running => Running,
        BackupStatus.Succeeded => Succeeded,
        BackupStatus.Failed => Failed,
        BackupStatus.Cancelled => Cancelled,
        BackupStatus.Interrupted => Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
