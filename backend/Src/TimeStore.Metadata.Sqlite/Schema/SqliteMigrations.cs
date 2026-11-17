namespace TimeStore.Metadata.Sqlite.Schema;

/// <summary>
/// Ordered schema migrations; the index + 1 is the version stored in <c>schema_migrations</c>. Append only: never
/// edit a released migration. Document every schema change in <c>docs/database.md</c>.
/// Conventions: STRICT tables, snake_case, ids as BLOB (Guid), timestamps as INTEGER unix milliseconds (UTC),
/// keys and names with BINARY collation (S3 orders keys by UTF-8 bytes).
/// </summary>
internal static class SqliteMigrations
{
    public const string EnableWal = "PRAGMA journal_mode = WAL;";

    public const string CreateHistory = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version    INTEGER PRIMARY KEY,
            applied_at INTEGER NOT NULL
        ) STRICT;
        """;

    public const string CurrentVersion = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";

    public const string RecordVersion = "INSERT INTO schema_migrations (version, applied_at) VALUES ($version, $appliedAt);";
    public const string VersionParameter = "$version";
    public const string AppliedAtParameter = "$appliedAt";

    private const string V1Buckets = """
        CREATE TABLE buckets (
            id         BLOB    NOT NULL PRIMARY KEY,
            name       TEXT    NOT NULL COLLATE BINARY,
            created_at INTEGER NOT NULL,
            CONSTRAINT ux_buckets_name UNIQUE (name)
        ) STRICT;
        """;

    // Objects (phase 2): one row per current object. WITHOUT ROWID + (bucket_id, key) primary key stores rows in
    // listing order, so GET is one B-tree lookup and listings are range scans.
    private const string V2Objects = """
        CREATE TABLE objects (
            bucket_id          BLOB    NOT NULL,
            key                TEXT    NOT NULL COLLATE BINARY,
            version_id         BLOB    NOT NULL,
            blob_id            BLOB    NOT NULL,
            node_id            TEXT    NOT NULL,
            size               INTEGER NOT NULL,
            etag               TEXT    NOT NULL,
            content_type       TEXT    NOT NULL,
            checksum_algorithm TEXT,
            checksum           BLOB,
            headers            TEXT,
            user_metadata      TEXT,
            created_at         INTEGER NOT NULL,
            PRIMARY KEY (bucket_id, key),
            CONSTRAINT fk_objects_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id)
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_objects_blob ON objects (blob_id);
        """;

    // Access keys (phase 2f): the secret is AES-GCM ciphertext (ISecretProtector, associated data = access key id).
    private const string V3AccessKeys = """
        CREATE TABLE access_keys (
            access_key_id     TEXT    NOT NULL PRIMARY KEY COLLATE BINARY,
            secret_ciphertext BLOB    NOT NULL,
            description       TEXT    NOT NULL,
            created_at        INTEGER NOT NULL,
            disabled_at       INTEGER
        ) STRICT;
        """;

    // Multipart (phase 3). objects is rebuilt (SQLite cannot relax NOT NULL): a multipart object has no single blob
    // (blob_id/node_id NULL, parts_count set) and its manifest lives in object_parts, keyed by version so versioning
    // (phase 6) needs no change. upload_id records which upload produced the row (retried Complete).
    private const string V4Multipart = """
        CREATE TABLE objects_v4 (
            bucket_id          BLOB    NOT NULL,
            key                TEXT    NOT NULL COLLATE BINARY,
            version_id         BLOB    NOT NULL,
            blob_id            BLOB,
            node_id            TEXT,
            size               INTEGER NOT NULL,
            etag               TEXT    NOT NULL,
            content_type       TEXT    NOT NULL,
            checksum_algorithm TEXT,
            checksum           BLOB,
            headers            TEXT,
            user_metadata      TEXT,
            created_at         INTEGER NOT NULL,
            parts_count        INTEGER,
            upload_id          BLOB,
            checksum_type      TEXT,
            PRIMARY KEY (bucket_id, key),
            CONSTRAINT fk_objects_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id),
            CONSTRAINT ck_objects_data CHECK ((blob_id IS NULL) = (parts_count IS NOT NULL))
        ) STRICT, WITHOUT ROWID;
        INSERT INTO objects_v4 (bucket_id, key, version_id, blob_id, node_id, size, etag, content_type,
                                checksum_algorithm, checksum, headers, user_metadata, created_at)
        SELECT bucket_id, key, version_id, blob_id, node_id, size, etag, content_type,
               checksum_algorithm, checksum, headers, user_metadata, created_at FROM objects;
        DROP TABLE objects;
        ALTER TABLE objects_v4 RENAME TO objects;
        CREATE INDEX ix_objects_blob ON objects (blob_id);

        CREATE TABLE object_parts (
            version_id  BLOB    NOT NULL,
            part_number INTEGER NOT NULL,
            offset      INTEGER NOT NULL,
            blob_id     BLOB    NOT NULL,
            node_id     TEXT    NOT NULL,
            size        INTEGER NOT NULL,
            etag        TEXT    NOT NULL,
            checksum    BLOB,
            PRIMARY KEY (version_id, part_number)
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_object_parts_blob ON object_parts (blob_id);

        CREATE TABLE multipart_uploads (
            upload_id          BLOB    NOT NULL PRIMARY KEY,
            bucket_id          BLOB    NOT NULL,
            key                TEXT    NOT NULL COLLATE BINARY,
            content_type       TEXT    NOT NULL,
            checksum_algorithm TEXT,
            checksum_type      TEXT,
            headers            TEXT,
            user_metadata      TEXT,
            created_at         INTEGER NOT NULL,
            CONSTRAINT fk_uploads_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id)
        ) STRICT;
        CREATE INDEX ix_uploads_key ON multipart_uploads (bucket_id, key, upload_id);
        CREATE INDEX ix_uploads_created ON multipart_uploads (created_at);

        CREATE TABLE upload_parts (
            upload_id   BLOB    NOT NULL,
            part_number INTEGER NOT NULL,
            blob_id     BLOB    NOT NULL,
            node_id     TEXT    NOT NULL,
            size        INTEGER NOT NULL,
            etag        TEXT    NOT NULL,
            checksum    BLOB,
            created_at  INTEGER NOT NULL,
            PRIMARY KEY (upload_id, part_number),
            CONSTRAINT fk_parts_upload FOREIGN KEY (upload_id) REFERENCES multipart_uploads (upload_id) ON DELETE CASCADE
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_upload_parts_blob ON upload_parts (blob_id);
        """;

    // Admin sign-in (end of phase 2): a role per access key (sign-in with access key id + secret) and server-side
    // sessions; only the SHA-256 of a session token is stored.
    private const string V5AdminSessions = """
        ALTER TABLE access_keys ADD COLUMN role TEXT NOT NULL DEFAULT 'none';

        CREATE TABLE admin_sessions (
            token_hash    BLOB    NOT NULL PRIMARY KEY,
            access_key_id TEXT    NOT NULL COLLATE BINARY,
            created_at    INTEGER NOT NULL,
            expires_at    INTEGER NOT NULL
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_admin_sessions_key ON admin_sessions (access_key_id);
        CREATE INDEX ix_admin_sessions_expiry ON admin_sessions (expires_at);
        """;

    // Storage metrics (step 7e): object count and bytes per bucket, kept by triggers in the transaction of every object
    // write (insert, replace through the upsert's UPDATE, delete), so the admin page never scans objects. Backfilled
    // once here. A bucket's row comes with the bucket and goes with it (FK cascade; the writer enables foreign keys).
    private const string V6BucketUsage = """
        CREATE TABLE bucket_usage (
            bucket_id    BLOB    NOT NULL PRIMARY KEY,
            object_count INTEGER NOT NULL,
            total_bytes  INTEGER NOT NULL,
            CONSTRAINT fk_usage_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id) ON DELETE CASCADE
        ) STRICT, WITHOUT ROWID;

        INSERT INTO bucket_usage (bucket_id, object_count, total_bytes)
            SELECT b.id, COUNT(o.key), COALESCE(SUM(o.size), 0)
            FROM buckets b LEFT JOIN objects o ON o.bucket_id = b.id
            GROUP BY b.id;

        CREATE TRIGGER tr_buckets_usage_insert AFTER INSERT ON buckets
        BEGIN
            INSERT INTO bucket_usage (bucket_id, object_count, total_bytes) VALUES (NEW.id, 0, 0);
        END;

        CREATE TRIGGER tr_objects_usage_insert AFTER INSERT ON objects
        BEGIN
            UPDATE bucket_usage SET object_count = object_count + 1, total_bytes = total_bytes + NEW.size
            WHERE bucket_id = NEW.bucket_id;
        END;

        CREATE TRIGGER tr_objects_usage_update AFTER UPDATE OF size ON objects
        BEGIN
            UPDATE bucket_usage SET total_bytes = total_bytes - OLD.size + NEW.size WHERE bucket_id = NEW.bucket_id;
        END;

        CREATE TRIGGER tr_objects_usage_delete AFTER DELETE ON objects
        BEGIN
            UPDATE bucket_usage SET object_count = object_count - 1, total_bytes = total_bytes - OLD.size
            WHERE bucket_id = OLD.bucket_id;
        END;
        """;

    // Versioning (step 6a): objects keeps the current object only; object_versions holds every noncurrent version and
    // every delete marker. Rows written so far are null versions (written unversioned, as in Amazon S3). version_id
    // (UUIDv7, big-endian) orders versions by time; object_parts stays keyed by it, so manifests never move.
    // superseded_at: when a version became noncurrent (lifecycle, step 6c).
    private const string V7Versioning = """
        ALTER TABLE buckets ADD COLUMN versioning TEXT;
        ALTER TABLE objects ADD COLUMN null_version INTEGER NOT NULL DEFAULT 1;

        CREATE TABLE object_versions (
            bucket_id          BLOB    NOT NULL,
            key                TEXT    NOT NULL COLLATE BINARY,
            version_id         BLOB    NOT NULL,
            null_version       INTEGER NOT NULL,
            is_delete_marker   INTEGER NOT NULL,
            blob_id            BLOB,
            node_id            TEXT,
            size               INTEGER NOT NULL,
            etag               TEXT    NOT NULL,
            content_type       TEXT    NOT NULL,
            checksum_algorithm TEXT,
            checksum           BLOB,
            headers            TEXT,
            user_metadata      TEXT,
            created_at         INTEGER NOT NULL,
            parts_count        INTEGER,
            upload_id          BLOB,
            checksum_type      TEXT,
            superseded_at      INTEGER,
            PRIMARY KEY (bucket_id, key, version_id),
            CONSTRAINT fk_versions_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id),
            CHECK (is_delete_marker = 1 OR (blob_id IS NULL) = (parts_count IS NOT NULL))
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_object_versions_blob ON object_versions (blob_id);
        CREATE INDEX ix_object_versions_superseded ON object_versions (superseded_at);

        ALTER TABLE bucket_usage ADD COLUMN version_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE bucket_usage ADD COLUMN version_bytes INTEGER NOT NULL DEFAULT 0;

        CREATE TRIGGER tr_versions_usage_insert AFTER INSERT ON object_versions
        BEGIN
            UPDATE bucket_usage SET version_count = version_count + 1, version_bytes = version_bytes + NEW.size
            WHERE bucket_id = NEW.bucket_id;
        END;

        CREATE TRIGGER tr_versions_usage_delete AFTER DELETE ON object_versions
        BEGIN
            UPDATE bucket_usage SET version_count = version_count - 1, version_bytes = version_bytes - OLD.size
            WHERE bucket_id = OLD.bucket_id;
        END;
        """;

    // Lifecycle (step 6c): one row per rule, in configuration order; the bucket takes its rules with it.
    private const string V8Lifecycle = """
        CREATE TABLE lifecycle_rules (
            bucket_id                    BLOB    NOT NULL,
            position                     INTEGER NOT NULL,
            rule_id                      TEXT    NOT NULL,
            enabled                      INTEGER NOT NULL,
            prefix                       TEXT    NOT NULL,
            prefix_in_rule               INTEGER NOT NULL,
            size_greater_than            INTEGER,
            size_less_than               INTEGER,
            expiration_days              INTEGER,
            expiration_date              INTEGER,
            expired_object_delete_marker INTEGER NOT NULL,
            noncurrent_days              INTEGER,
            newer_noncurrent_versions    INTEGER,
            abort_upload_days            INTEGER,
            PRIMARY KEY (bucket_id, position),
            CONSTRAINT fk_lifecycle_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id) ON DELETE CASCADE
        ) STRICT, WITHOUT ROWID;
        """;

    // Bucket policies (step 6d): the document as the client sent it, and the S3 scope of every access key (full: allowed
    // unless a policy denies; policy: allowed only where a policy allows).
    private const string V9BucketPolicies = """
        CREATE TABLE bucket_policies (
            bucket_id  BLOB    NOT NULL PRIMARY KEY,
            document   TEXT    NOT NULL,
            updated_at INTEGER NOT NULL,
            CONSTRAINT fk_policy_bucket FOREIGN KEY (bucket_id) REFERENCES buckets (id) ON DELETE CASCADE
        ) STRICT, WITHOUT ROWID;

        ALTER TABLE access_keys ADD COLUMN s3_scope TEXT NOT NULL DEFAULT 'full';
        """;

    public static readonly string[] All =
        [V1Buckets, V2Objects, V3AccessKeys, V4Multipart, V5AdminSessions, V6BucketUsage, V7Versioning, V8Lifecycle, V9BucketPolicies];
}
