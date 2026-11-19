using TimeStore.Primitives;

namespace TimeStore.Application.Features.Backups;

public static class BackupErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string Active = "BACKUP_ACTIVE";
        public const string NotFound = "BACKUP_NOT_FOUND";
        public const string InvalidTarget = "BACKUP_INVALID_TARGET";
        public const string TargetNotConfigured = "BACKUP_TARGET_NOT_CONFIGURED";
        public const string InvalidCompression = "BACKUP_INVALID_COMPRESSION";
        public const string NotActive = "BACKUP_NOT_ACTIVE";
        public const string ArchiveNotFound = "BACKUP_ARCHIVE_NOT_FOUND";

        // Why a run failed (recorded on the run, not answered by a request).
        public const string LowDiskSpace = "BACKUP_LOW_DISK_SPACE";
        public const string BlobMissing = "BACKUP_BLOB_MISSING";
        public const string BlobCorrupt = "BACKUP_BLOB_CORRUPT";
        public const string Cancelled = "BACKUP_CANCELLED";
        public const string Interrupted = "BACKUP_INTERRUPTED";
        public const string Failed = "BACKUP_FAILED";

        // Restore command.
        public const string RestoreNotEmpty = "BACKUP_RESTORE_NOT_EMPTY";
        public const string RestoreInvalidArchive = "BACKUP_RESTORE_INVALID_ARCHIVE";
        public const string RestoreUnsupported = "BACKUP_RESTORE_UNSUPPORTED";
        public const string RestoreChecksumMismatch = "BACKUP_RESTORE_CHECKSUM_MISMATCH";
        public const string RestoreArchiveUnavailable = "BACKUP_RESTORE_ARCHIVE_UNAVAILABLE";
    }

    public static readonly AppError Active =
        AppError.Conflict(Codes.Active, "A backup is already queued or running; wait for it to finish or cancel it.");

    public static AppError NotFound(Guid runId) =>
        AppError.NotFound(Codes.NotFound, $"Backup '{runId}' was not found.");

    public static AppError InvalidTarget(string target) =>
        AppError.Validation(Codes.InvalidTarget, $"'{target}' is not a backup target this request accepts.");

    public static readonly AppError TargetNotConfigured = AppError.Validation(Codes.TargetNotConfigured,
        "No backup directory is configured; set TimeStore:Backup:Path (or download a backup as a stream).");

    public static readonly AppError InvalidCompression =
        AppError.Validation(Codes.InvalidCompression, "Compression must be 'zstd' or 'none'.");

    public static readonly AppError NotActive =
        AppError.Conflict(Codes.NotActive, "The backup has already finished.");

    public static readonly AppError ArchiveNotFound = AppError.NotFound(Codes.ArchiveNotFound,
        "This backup has no stored archive (it was streamed, did not succeed, or the file was removed).");

    public static AppError LowDiskSpace(long freeBytes, long minFreeBytes) => AppError.Conflict(Codes.LowDiskSpace,
        $"Only {freeBytes} bytes are free in the data directory; a backup needs at least {minFreeBytes} (TimeStore:Backup:MinFreeBytes).");

    /// <summary>The archive directory cannot take an archive as large as the data (uncompressed, the worst case).</summary>
    public static AppError LowArchiveSpace(long freeBytes, long neededBytes) => AppError.Conflict(Codes.LowDiskSpace,
        $"Only {freeBytes} bytes are free in the backup directory; this backup needs up to {neededBytes} (the data plus "
        + "TimeStore:Backup:MinFreeBytes). Free space, download and delete older archives, or lower TimeStore:Backup:Keep.");

    public static AppError BlobMissing(Guid blobId) =>
        AppError.Internal(Codes.BlobMissing, $"Blob {blobId:N} referenced by the metadata is missing from the data directory.");

    public static AppError BlobCorrupt(Guid blobId) =>
        AppError.Internal(Codes.BlobCorrupt, $"Blob {blobId:N} does not match its stored checksum or size.");

    public static readonly AppError Cancelled = AppError.Conflict(Codes.Cancelled, "The backup was cancelled.");

    public static readonly AppError Interrupted = AppError.Conflict(Codes.Interrupted,
        "The backup stopped before it finished: the server shut down or the client disconnected.");

    public static readonly AppError Failed =
        AppError.Internal(Codes.Failed, "The backup failed; the server log has the details.");

    public static readonly AppError RestoreNotEmpty = AppError.Conflict(Codes.RestoreNotEmpty,
        "The data directory is not empty; restore into an empty directory (a restore never merges or overwrites).");

    public static AppError RestoreInvalidArchive(string reason) =>
        AppError.Validation(Codes.RestoreInvalidArchive, $"The file is not a valid TimeStore backup: {reason}");

    public static AppError RestoreUnsupported(string reason) =>
        AppError.Validation(Codes.RestoreUnsupported, $"This TimeStore cannot restore the archive: {reason}");

    public static AppError RestoreChecksumMismatch(string entry) =>
        AppError.Validation(Codes.RestoreChecksumMismatch, $"Entry '{entry}' does not match its checksum; the archive is damaged.");

    public static AppError RestoreArchiveUnavailable(string archive, string reason) =>
        AppError.Validation(Codes.RestoreArchiveUnavailable, $"The archive {archive} cannot be read: {reason}");
}
