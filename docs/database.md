# Metadata database

Provider: SQLite (`TimeStore.Metadata.Sqlite`), file `data/metadata.db`. Update this file with every schema change.
Conventions: `.claude/rules/database.md`.

## Settings

| Setting | Value | Why |
|---|---|---|
| `journal_mode` | WAL | Readers never block the writer |
| `synchronous` | FULL (writer) | A committed object survives power loss; group commit recovers throughput |
| `foreign_keys` | ON (writer) | Integrity between objects, versions, uploads, parts |
| `busy_timeout` | 5000 ms (writer) | The single writer only waits on a checkpoint or the migrator |

The pragmas run once, on the single write connection (`SqliteWriter`); pooled read connections need none.

## Tables

### `schema_migrations`

| Column | Type | Notes |
|---|---|---|
| `version` | INTEGER PK | Index + 1 in `SqliteMigrations.All` |
| `applied_at` | INTEGER | Unix ms UTC |

### `buckets` (v1)

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB PK | UUIDv7, 16 bytes **big-endian** (RFC 9562 order, sorts by time): `AddId` / `GetId` |
| `name` | TEXT, BINARY | S3 bucket name, unique |
| `created_at` | INTEGER | Unix ms UTC |

Indexes: `ux_buckets_name` (UNIQUE name) — lookup by name, create-if-free (`ON CONFLICT DO NOTHING`), ordered list.

### `objects` (v2, rebuilt in v4)

| Column | Type | Notes |
|---|---|---|
| `bucket_id` | BLOB, PK part, FK → `buckets.id` | |
| `key` | TEXT BINARY, PK part | UTF-8 byte order = S3 listing order |
| `version_id` | BLOB | UUIDv7 (versioning history in phase 6) |
| `blob_id`, `node_id` | BLOB, TEXT, nullable | Single PUT: the `BlobLocator`; never a path. NULL for multipart objects |
| `size` | INTEGER | Object size (multipart: sum of the parts) |
| `etag` | TEXT | Hex MD5 without quotes (multipart `md5-N`, phase 3) |
| `content_type` | TEXT | Default `binary/octet-stream` |
| `checksum_algorithm`, `checksum` | TEXT, BLOB, nullable | Stored checksum (canonical byte order) |
| `headers`, `user_metadata` | TEXT, nullable | `name:value` + LF per entry (`ObjectHeaderText`) |
| `created_at` | INTEGER | Unix ms UTC |
| `parts_count` | INTEGER, nullable (v4) | Multipart objects: number of rows in `object_parts` |
| `upload_id` | BLOB, nullable (v4) | Multipart objects: the upload that produced the row (a retried Complete returns it) |
| `checksum_type` | TEXT, nullable (v4) | Multipart objects with a checksum: `COMPOSITE` or `FULL_OBJECT` |

`CHECK ((blob_id IS NULL) = (parts_count IS NOT NULL))`: a row is either one blob or a manifest.

`STRICT, WITHOUT ROWID`, primary key `(bucket_id, key)`: rows stored in listing order; `GET` is one lookup, listings
are range scans with skip-scan over common prefixes. Index `ix_objects_blob (blob_id)` for the orphan-blob sweep.

### `admin_sessions` (v5)

| Column | Type | Notes |
|---|---|---|
| `token_hash` | BLOB, PK | SHA-256 of the 256-bit session token (the token itself is only in the cookie) |
| `access_key_id` | TEXT BINARY | The signed-in key (root included); index `ix_admin_sessions_key` ends a key's sessions |
| `created_at`, `expires_at` | INTEGER | Unix ms UTC; index `ix_admin_sessions_expiry` for the purge |

`STRICT, WITHOUT ROWID`. v5 also adds `access_keys.role` (TEXT, default `none`; `viewer`, `admin`).

### `object_parts` (v4)

The manifest of a multipart object: parts are never copied into one blob.

| Column | Type | Notes |
|---|---|---|
| `version_id` | BLOB, PK part | `objects.version_id` (keyed by version: phase 6 versioning needs no change) |
| `part_number` | INTEGER, PK part | 1–10 000, ascending, gaps allowed |
| `offset` | INTEGER | Position of the part's first byte in the object |
| `blob_id`, `node_id`, `size`, `etag` | BLOB, TEXT, INTEGER, TEXT | The part's blob; `etag` = hex MD5 of the part |
| `checksum` | BLOB, nullable | The part's checksum (the object's algorithm) |

`STRICT, WITHOUT ROWID`; index `ix_object_parts_blob (blob_id)` for the orphan sweep.

### `multipart_uploads` (v4)

| Column | Type | Notes |
|---|---|---|
| `upload_id` | BLOB, PK | UUIDv7: sorts by initiation time (ListMultipartUploads order) |
| `bucket_id`, `key` | BLOB FK → `buckets.id`, TEXT BINARY | The object being uploaded |
| `content_type`, `headers`, `user_metadata` | TEXT | Given at creation, applied at completion |
| `checksum_algorithm`, `checksum_type` | TEXT, nullable | Every part must use this algorithm |
| `created_at` | INTEGER | Unix ms UTC; abandoned uploads are aborted by age (`ix_uploads_created`) |

`STRICT`; index `ix_uploads_key (bucket_id, key, upload_id)` for listing. Uploads do not keep a bucket from being deleted: they are aborted with it (Amazon S3).

### `upload_parts` (v4)

| Column | Type | Notes |
|---|---|---|
| `upload_id` | BLOB, PK part, FK → `multipart_uploads` ON DELETE CASCADE | |
| `part_number` | INTEGER, PK part | Re-uploading a number replaces the row |
| `blob_id`, `node_id`, `size`, `etag`, `checksum` | | As in `object_parts` |
| `created_at` | INTEGER | Unix ms UTC |

`STRICT, WITHOUT ROWID`; index `ix_upload_parts_blob (blob_id)`. Completion moves the listed parts to `object_parts`
and deletes the upload (and, by cascade, these rows) in one transaction.

### `access_keys` (v3)

| Column | Type | Notes |
|---|---|---|
| `access_key_id` | TEXT BINARY, PK | `TS` + 18 base32 characters; not a secret |
| `secret_ciphertext` | BLOB | AES-256-GCM via `ISecretProtector` (associated data = access key id): version, key id, nonce, tag, ciphertext. Never plain text |
| `description` | TEXT | Up to 256 characters, may be empty |
| `created_at` | INTEGER | Unix ms UTC |
| `disabled_at` | INTEGER, nullable | Set while the key is disabled (fails SigV4 with `InvalidAccessKeyId`) |

v9 adds `s3_scope` (TEXT, default `full`; `policy`: S3 access only where a bucket policy allows it, step 6d).

`STRICT`. The root key from configuration is not stored here (break-glass). The master key that decrypts
`secret_ciphertext` is configuration (`TimeStore:MasterKey` or `MasterKeyFile`), never stored with the data; a
database copy without it holds no usable secret.

### `bucket_usage` (v6)

| Column | Type | Notes |
|---|---|---|
| `bucket_id` | BLOB, PK, FK → `buckets.id` `ON DELETE CASCADE` | One row per bucket |
| `object_count` | INTEGER | Current objects |
| `total_bytes` | INTEGER | Sum of their `size` |

`STRICT, WITHOUT ROWID`. Kept by triggers in the transaction of every write: `tr_buckets_usage_insert` (new bucket →
zero row), `tr_objects_usage_insert`, `tr_objects_usage_update` (`UPDATE OF size`: the upsert that replaces an
object), `tr_objects_usage_delete`. Backfilled from `objects` by the migration. Read by `ListBucketUsageAsync` for
the metrics page (one row per bucket, never a scan of `objects`). Versioning (phase 6) must keep these triggers right
for versions and delete markers.

### `object_versions` (v7)

Every noncurrent version and every delete marker (also a latest one). `objects` keeps the current object only, so
listings and GETs never read this table.

| Column | Type | Notes |
|---|---|---|
| `bucket_id`, `key`, `version_id` | PK | `version_id` is the internal UUIDv7 (big-endian): newest first is `version_id DESC` |
| `null_version` | INTEGER | 1: the client sees version id `null` (written unversioned or suspended); at most one per key across both tables |
| `is_delete_marker` | INTEGER | 1: no data (blob, parts, size 0) |
| object columns | | As in `objects` (`blob_id` … `checksum_type`); a multipart version keeps its `object_parts` rows |
| `superseded_at` | INTEGER, nullable | When the version became noncurrent (lifecycle); NULL for a delete marker that is the latest |

`STRICT, WITHOUT ROWID`; indexes `ix_object_versions_blob (blob_id)` (orphan sweep), `ix_object_versions_superseded`.
`CHECK (is_delete_marker = 1 OR (blob_id IS NULL) = (parts_count IS NOT NULL))`. v7 also adds `buckets.versioning`
(NULL never enabled, `Enabled`, `Suspended`), `objects.null_version` (default 1: existing rows are null versions) and
`bucket_usage.version_count` / `version_bytes` (triggers `tr_versions_usage_insert/delete`).

Invariant: an `objects` row is its key's latest version; versions are ordered by internal id, and a new version's id
always sorts after the key's newest (`VersionOrder.Next`). Writes read `buckets.versioning` inside their transaction.

### `lifecycle_rules` (v8)

| Column | Type | Notes |
|---|---|---|
| `bucket_id`, `position` | PK; FK → `buckets.id` `ON DELETE CASCADE` | Rules in configuration order |
| `rule_id` | TEXT | ≤ 255 characters, unique per bucket (checked by the service) |
| `enabled` | INTEGER | `Status` |
| `prefix`, `size_greater_than`, `size_less_than` | TEXT, INTEGER, INTEGER | The filter |
| `expiration_days`, `expiration_date` | INTEGER | Current version expiration (date: unix ms, midnight UTC) |
| `expired_object_delete_marker` | INTEGER | Remove lone delete markers |
| `noncurrent_days`, `newer_noncurrent_versions` | INTEGER | Noncurrent version expiration |
| `abort_upload_days` | INTEGER | Abort incomplete multipart uploads |

`STRICT, WITHOUT ROWID`. A PUT replaces all rows of the bucket in one transaction.

### `bucket_policies` (v9)

| Column | Type | Notes |
|---|---|---|
| `bucket_id` | BLOB PK; FK → `buckets.id` `ON DELETE CASCADE` | One policy per bucket |
| `document` | TEXT | The IAM JSON exactly as sent (≤ 20 KB, validated before it is stored) |
| `updated_at` | INTEGER | Unix ms UTC |

`STRICT`. Parsed policies are cached per bucket name in memory (`BucketPolicyCache`) and dropped on PUT, DELETE and
bucket deletion.

### `backup_runs` (v10; v11 drops `include_master_key`)

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB PK | UUIDv7: the primary key orders runs by time (list newest first, wake queued oldest first) |
| `status` | TEXT | `queued`, `running`, `succeeded`, `failed`, `cancelled`, `interrupted` (CHECK) |
| `trigger_type` | TEXT | `manual`, `api`, `schedule` |
| `target_type`, `compression` | TEXT | `directory` \| `stream`; `zstd` \| `none` |
| `started_by` | TEXT | Access key that started it |
| `queued_at`, `started_at`, `finished_at` | INTEGER | Unix ms UTC |
| `snapshot_at` | INTEGER | Instant T of the metadata snapshot |
| `blobs_total`, `blobs_done`, `bytes_total`, `bytes_done` | INTEGER | Progress (written every 2 s) |
| `archive_name`, `archive_size`, `archive_sha256` | TEXT, INTEGER, TEXT | Set at start (name) and on success |
| `error_code`, `error_message` | TEXT | Why it did not succeed (an `AppError` code; message ≤ 1024 characters) |

`STRICT`. `ux_backup_runs_active`: a partial unique index (`WHERE status IN ('queued', 'running')`, one constant key)
allows at most one active run; an insert that hits it means "a backup is already active". Running rows are marked
`interrupted` at startup and never re-run. Snapshots (`VACUUM INTO tmp/backup-<id>.db`) leave this table and
`admin_sessions` empty, so a restored server starts without history or sessions.

### `replication_rules` (v12)

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB PK | UUIDv7; kept when the rule is edited |
| `bucket_id` | BLOB | UNIQUE (one rule per bucket, R1); FK `buckets` `ON DELETE CASCADE` |
| `endpoint`, `target_bucket`, `region`, `access_key_id` | TEXT | The destination (`https://host[:port]`, path-style) |
| `secret_ciphertext` | BLOB | `ISecretProtector`, associated data = `bucket_id`; never returned or logged |
| `prefix` | TEXT BINARY | Only keys starting with it (`""` = all) |
| `replicate_deletes` | INTEGER | 0/1 |
| `created_at`, `updated_at` | INTEGER | Unix ms UTC |
| `paused_until`, `failures`, `last_error` | INTEGER, INTEGER, TEXT | Backoff after temporary destination failures; reset by a success |
| `last_success_at` | INTEGER | Last change that reached the destination |

### `replication_queue` (v12)

The outbox: one row per change, inserted by `CommitObjectAsync`, `CompleteUploadAsync` and the deletes in the commit's
own transaction (no row at all when no rule exists: the store keeps a rule count). The worker sends the key's current
state and deletes the key's rows up to the newest id it had read.

| Column | Type | Notes |
|---|---|---|
| `id` | INTEGER PK AUTOINCREMENT | Order of the changes; never reused |
| `rule_id` | BLOB | FK `replication_rules` `ON DELETE CASCADE` |
| `object_key` | TEXT BINARY | |
| `operation` | TEXT | `put` \| `delete` (CHECK); deletes are only queued when the rule sends them |
| `created_at`, `next_attempt` | INTEGER | Unix ms UTC; `next_attempt` delays one key (its blob could not be read) |
| `attempts`, `status`, `last_error` | INTEGER, TEXT, TEXT | `pending` \| `failed` (CHECK); failed only for a permanent refusal |

Indexes: `ix_replication_queue_due (status, next_attempt, id)`, `ix_replication_queue_key (rule_id, object_key, id)`.
Backup snapshots leave both tables empty: a restored server must not push its older state over a newer replica.

### `pending_blob_deletions` (v13)

Blobs no metadata row references any more, waiting to be deleted (docs/research/blob-retention.md). Inserted by the
write that releases a blob, in its own transaction (`CommitObjectAsync`, `CompleteUploadAsync`, `CommitPartAsync`,
`AbortUploadAsync`, `DeleteBucketIfEmptyAsync`, `DeleteObjectVersionsAsync`); deleted by the collector once
max(`Blobs:DeleteDelaySeconds`, `Blobs:RetainDeletedDays`) has passed since `released_at`. The orphan sweep treats
these blobs as referenced.

| Column | Type | Notes |
|---|---|---|
| `blob_id` | BLOB PK | The released blob (`WITHOUT ROWID`) |
| `node_id` | TEXT | The node whose disk holds it; each node collects its own |
| `size` | INTEGER | Bytes, for the retained-bytes metric |
| `released_at` | INTEGER | Unix ms UTC; the wait is computed from configuration, so raising the retention protects blobs released before |

Index: `ix_pending_blob_deletions_released (node_id, released_at)`. Backup snapshots keep this table.

## Planned

- Sizing estimate: 300–500 bytes per object row + index (100M objects ≈ 30–50 GB).
