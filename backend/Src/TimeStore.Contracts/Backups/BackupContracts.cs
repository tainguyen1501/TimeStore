namespace TimeStore.Contracts.Backups;

/// <summary>Starts a backup.</summary>
/// <param name="Target">Where the archive goes: <c>directory</c> (the default; <c>TimeStore:Backup:Path</c>).</param>
/// <param name="Compression"><c>zstd</c> or <c>none</c>; the server's default (<c>TimeStore:Backup:Compression</c>) when null.</param>
/// <param name="Content">
/// <c>full</c> (the default: the metadata and every blob) or <c>metadata</c> (the metadata only: seconds, restored onto
/// the same data directory; needs <c>TimeStore:Blobs:RetainDeletedDays</c>).
/// </param>
public sealed record StartBackupRequest(string? Target = null, string? Compression = null, string? Content = null);

/// <summary>One backup run.</summary>
/// <param name="Status"><see cref="BackupRunStatuses"/>.</param>
/// <param name="Trigger"><c>manual</c>, <c>api</c> or <c>schedule</c>.</param>
/// <param name="Target"><c>stream</c> or <c>directory</c>.</param>
/// <param name="StartedBy">The access key that started it.</param>
/// <param name="SnapshotAt">Instant T: the archive holds the server as it was then.</param>
/// <param name="ArchiveSha256">Lowercase hex SHA-256 of the archive, to check a copy.</param>
/// <param name="ErrorCode">Why it failed (an error code of docs/api/admin.md), or null.</param>
public sealed record BackupRunResponse(
    Guid Id,
    string Status,
    string Trigger,
    string Target,
    string Compression,
    string StartedBy,
    DateTime QueuedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    DateTime? SnapshotAt,
    long BlobsTotal,
    long BlobsDone,
    long BytesTotal,
    long BytesDone,
    string? ArchiveName,
    long? ArchiveSize,
    string? ArchiveSha256,
    string? ErrorCode,
    string? ErrorMessage,
    string Content = "full");

public static class BackupRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The server stopped while the run was running; it is never re-run automatically.</summary>
    public const string Interrupted = "interrupted";
}
