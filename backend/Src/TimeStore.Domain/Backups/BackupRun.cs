namespace TimeStore.Domain.Backups;

/// <summary>
/// One backup, however it was started (a click, the API, later a schedule). The row is the source of truth: it exists
/// before any work starts, and the worker or the streaming request only moves it forward (docs/research/backup.md).
/// </summary>
/// <param name="Trigger">Who started it: <see cref="BackupTriggers"/>.</param>
/// <param name="TargetType">Where the archive goes: <see cref="BackupTargetTypes"/>.</param>
/// <param name="Compression"><see cref="BackupCompressions"/>.</param>
/// <param name="StartedBy">The access key that asked for it (empty for the system).</param>
/// <param name="SnapshotAt">Instant T: the archive holds the server as it was then. Set once the snapshot exists.</param>
/// <param name="ArchiveName">File name of the archive (set when the run starts writing).</param>
/// <param name="ArchiveSha256">Lowercase hex SHA-256 of the archive as written (set on success).</param>
/// <param name="ErrorCode">Stable code of the failure (an <c>AppError</c> code), or null.</param>
public sealed record BackupRun(
    Guid Id,
    BackupStatus Status,
    string Trigger,
    string TargetType,
    string Compression,
    string StartedBy,
    DateTime QueuedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    DateTime? SnapshotAt,
    BackupProgress Progress,
    string? ArchiveName,
    long? ArchiveSize,
    string? ArchiveSha256,
    string? ErrorCode,
    string? ErrorMessage)
{
    public const int StartedByMaxLength = 128;
    public const int ArchiveNameMaxLength = 255;
    public const int ErrorMessageMaxLength = 1024;

    /// <summary>What the archive holds: <see cref="BackupContents"/> (the whole server, or the metadata only).</summary>
    public string Content { get; init; } = BackupContents.Full;

    /// <summary>A new run waiting for the worker (or for the request that streams it).</summary>
    public static BackupRun Queue(
        string trigger, string targetType, string compression, string startedBy, DateTime now,
        string content = BackupContents.Full) =>
        new(Guid.CreateVersion7(), BackupStatus.Queued, trigger, targetType, compression, startedBy,
            now, null, null, null, BackupProgress.None, null, null, null, null, null) { Content = content };

    public bool IsActive => Status is BackupStatus.Queued or BackupStatus.Running;
}

/// <summary>How far a run is: blobs and bytes copied out of the totals the snapshot references.</summary>
public sealed record BackupProgress(long BlobsTotal, long BlobsDone, long BytesTotal, long BytesDone)
{
    public static readonly BackupProgress None = new(0, 0, 0, 0);
}

public enum BackupStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>The process stopped while it ran (crash, restart). Never re-run automatically.</summary>
    Interrupted,
}

/// <summary>How a run ended: the terminal status, the archive (on success) and the error (otherwise).</summary>
public sealed record BackupOutcome(
    BackupStatus Status,
    DateTime FinishedAt,
    BackupProgress Progress,
    long? ArchiveSize,
    string? ArchiveSha256,
    string? ErrorCode,
    string? ErrorMessage);

public static class BackupTriggers
{
    /// <summary>Started by a person in the UI.</summary>
    public const string Manual = "manual";

    /// <summary>Started by a program through the admin API (curl, a hook, TimeOps).</summary>
    public const string Api = "api";

    /// <summary>Started by a schedule (stage B3).</summary>
    public const string Schedule = "schedule";

    public static bool IsKnown(string? trigger) => trigger is Manual or Api or Schedule;
}

public static class BackupTargetTypes
{
    /// <summary>Written straight into the HTTP response; nothing is kept on the server.</summary>
    public const string Stream = "stream";

    /// <summary>A file in the configured backup directory (outside the data directory).</summary>
    public const string Directory = "directory";
}

/// <summary>What a backup archive holds (docs/research/blob-retention.md, stage B5b).</summary>
public static class BackupContents
{
    /// <summary>The metadata and every blob it references: restores anywhere, on its own.</summary>
    public const string Full = "full";

    /// <summary>
    /// The metadata only (seconds, small): restored onto the data directory it came from, whose blobs released after
    /// the backup are still kept (<c>Blobs:RetainDeletedDays</c>).
    /// </summary>
    public const string Metadata = "metadata";

    public static bool IsKnown(string? content) => content is Full or Metadata;
}

public static class BackupCompressions
{
    public const string Zstd = "zstd";
    public const string None = "none";

    public static bool IsKnown(string? compression) => compression is Zstd or None;
}
