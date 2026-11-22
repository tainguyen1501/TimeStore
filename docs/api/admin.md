# Admin API (`/api/v1`, port 9001)

Bound to `127.0.0.1` by default (`TimeStore:AdminAddress`). JSON (camelCase). Errors: `{ "code", "message",
"traceId" }` with the HTTP status of the error; clients branch on `code`.

## Sign-in

Every `/api/v1` endpoint except `auth/login` needs a session; health endpoints are open. The backup API
(`/api/v1/backups`) is the exception: signed requests only (see "Backups").

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| POST | `/api/v1/auth/login` | `{ "accessKeyId", "secretKey" }` | `200 SessionResponse` + cookie `ts_session`, `Cache-Control: no-store` | 401 `AUTH_INVALID_CREDENTIALS`, 429 `AUTH_TOO_MANY_ATTEMPTS` (`Retry-After`) |
| POST | `/api/v1/auth/logout` | — | `204`, cookie cleared, session ended | — |
| GET | `/api/v1/auth/me` | — | `200 SessionResponse` | 401 |

`SessionResponse`: `{ "accessKeyId", "role": "admin" \| "viewer", "expiresAt": "…Z" }`.

- **Who may sign in:** the root key from configuration (always `admin`) and keys with role `viewer` or `admin`
  (`none`, the default, is S3 only). Unknown keys, wrong secrets, disabled keys and keys without a role all get the
  same 401.
- **Session:** the cookie holds a 256-bit random token (`HttpOnly`, `SameSite=Strict`, `Path=/`, `Secure` over HTTPS or
  with `Admin:SecureCookies`); the server stores only its SHA-256. Lifetime `Admin:SessionHours` (24). Disabling,
  deleting or changing the role of a key ends its sessions at once.
- **Roles:** `viewer` may only read (`GET`); anything else is 403 `AUTH_FORBIDDEN`. `admin` may do everything.
- **CSRF:** `POST`/`PUT`/`PATCH`/`DELETE` must carry `Origin` equal to the server's scheme and host, or
  `Sec-Fetch-Site: same-origin` (browsers send both); otherwise 403 `AUTH_CROSS_SITE`.
- **Throttling:** after 5 failures in 15 minutes for one key or one client address, each attempt waits, doubling from
  1 s up to 15 min (429 with `Retry-After`).

## UI and OpenAPI

- `GET`/`HEAD` on any path outside `/api`, `/health` and `/openapi` serves the admin UI embedded in the binary (or
  from `TimeStore:Admin:UiPath`): a file that exists, 404 for a missing file name, `index.html` for anything else
  (client routes). `/assets/*` is cached for a year (`immutable`), `index.html` is `no-cache`. UI responses carry
  `Content-Security-Policy`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and are compressed
  (Brotli/gzip); API responses never are. A build without the UI answers 404 with a hint.
- `GET /openapi/v1.json`: the OpenAPI 3.1 document of this API, only when `TimeStore:Admin:OpenApi` is true
  (Development). The committed copy `docs/api/openapi.v1.json` is the source of the frontend types and is checked
  against the served document by `OpenApiDocumentTests`.

## Health

`GET /health/live` (both ports) → `200 { "status": "healthy", "nodeId": "node-1" }`

## Buckets

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/buckets` | — | `200 BucketResponse[]` (name order) | — |
| POST | `/api/v1/buckets` | `{ "name" }` | `201 BucketResponse` | 400 `BUCKET_INVALID_NAME`, 409 `BUCKET_ALREADY_EXISTS` |
| DELETE | `/api/v1/buckets/{name}` | — | `204` | 404 `BUCKET_NOT_FOUND`, 409 `BUCKET_NOT_EMPTY` |

`BucketResponse`: `{ "id": "uuid", "name": "photos", "createdAt": "2026-11-16T02:58:56.9Z", "versioning": "Unversioned" }`
(`Unversioned`, `Enabled`, `Suspended`).

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| PUT | `/api/v1/buckets/{name}/versioning` | `{ "status": "Enabled" \| "Suspended" }` | `200 BucketResponse` | 400 `BUCKET_INVALID_VERSIONING`, 404 `BUCKET_NOT_FOUND` |

## Lifecycle

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/buckets/{name}/lifecycle` | — | `200 LifecycleConfigurationDto` | 404 `LIFECYCLE_NOT_FOUND`, `BUCKET_NOT_FOUND` |
| PUT | `/api/v1/buckets/{name}/lifecycle` | `LifecycleConfigurationDto` (replaces all rules) | `200 LifecycleConfigurationDto` (ids filled in) | 400 `LIFECYCLE_INVALID`, `LIFECYCLE_MALFORMED`, 404 |
| DELETE | `/api/v1/buckets/{name}/lifecycle` | — | `204` (also without a configuration) | 404 `BUCKET_NOT_FOUND` |
| POST | `/api/v1/maintenance/lifecycle` | — | `200 LifecycleRunResponse` (`expired`, `noncurrentRemoved`, `deleteMarkersRemoved`, `uploadsAborted`, `startedAt`, `finishedAt`) | — |

`LifecycleConfigurationDto`: `{ "rules": [{ "id", "enabled", "prefix", "sizeGreaterThan", "sizeLessThan",
"expirationDays", "expirationDate", "expiredObjectDeleteMarker", "noncurrentDays", "newerNoncurrentVersions",
"abortIncompleteUploadDays" }] }` (see `docs/research/s3-lifecycle.md` for the rules).

## Bucket policy

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/buckets/{name}/policy` | — | `200 BucketPolicyResponse` | 404 `POLICY_NOT_FOUND`, `BUCKET_NOT_FOUND` |
| PUT | `/api/v1/buckets/{name}/policy` | `{ "document": "<IAM JSON>" }` (replaces the policy) | `200 BucketPolicyResponse` | 400 `POLICY_MALFORMED`, `POLICY_NOT_IMPLEMENTED` (`NotPrincipal`), 404 |
| DELETE | `/api/v1/buckets/{name}/policy` | — | `204` (also without a policy) | 404 `BUCKET_NOT_FOUND` |

| GET | `/api/v1/buckets/{name}/public` | — | `200 { "prefixes": ["", "img/"] }` | 404 `BUCKET_NOT_FOUND` |
| PUT | `/api/v1/buckets/{name}/public` | `{ "prefix": "" \| "folder/", "isPublic": true }` | `200 { "prefixes" }` | 400 `POLICY_INVALID_PUBLIC_PREFIX`, `POLICY_MALFORMED` (result over 20 KB), 404, 409 `POLICY_CONFLICT` |

`/public` is "Make public": TimeStore keeps one statement (`Sid: TimeStorePublicRead`, everyone `s3:GetObject` on
`arn:aws:s3:::bucket/<prefix>*`) and adds or removes prefixes in it; other statements are never changed, and the
policy is removed when nothing is left. `""` is the whole bucket; a folder ends in `/` and may not contain `*` or `?`.
The edit is a compare-and-swap on the stored document, so it never overwrites a concurrent change.

`BucketPolicyResponse`: `{ "document": "…", "isPublic": true }`; `isPublic` when a statement allows everyone (`*`)
without conditions. What a policy may contain and how it is evaluated: `docs/research/s3-bucket-policy.md`.

## Objects (object browser)

Keys always travel in the query string (`key=`, URL-encoded), never in the path. Folders are a view over the `/`
delimiter. `versionId` is always null until versioning (roadmap 6a). Writes need the `admin` role.

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/buckets/{bucket}/objects?prefix=&startAfter=&limit=` | — | `200 ObjectListResponse` (limit 1–1000, default 200) | 404 `BUCKET_NOT_FOUND` |
| GET | `/api/v1/buckets/{bucket}/objects/details?key=` | — | `200 ObjectDetailsResponse` | 404 `BUCKET_NOT_FOUND`, `OBJECT_NOT_FOUND` |
| GET | `/api/v1/buckets/{bucket}/objects/content?key=` | — | `200` the bytes, `Content-Disposition: attachment`, `Content-Security-Policy: sandbox`, `nosniff`, `no-store` | 404 |
| PUT | `/api/v1/buckets/{bucket}/objects/content?key=` | raw bytes (`Content-Type` stored, `Content-Length` required, ≤ 5 GiB) | `200 ObjectWrittenResponse` | 400 `OBJECT_LENGTH_REQUIRED`, `OBJECT_TOO_LARGE`, `OBJECT_INVALID_KEY`, 404 |
| POST | `/api/v1/buckets/{bucket}/objects/delete` | `{ "keys"?: [..≤1000], "prefixes"?: ["folder/"] }` | `200 { "deleted": n }` (prefixes: every object under them) | 400 `OBJECT_TOO_MANY_KEYS`, `OBJECT_BROWSER_INVALID_PREFIX`, `OBJECT_BROWSER_NOTHING_TO_DELETE`, 404 |
| POST | `/api/v1/buckets/{bucket}/uploads` | `{ "key", "contentType"? }` | `200 { "uploadId" }` | 400, 404 |
| PUT | `/api/v1/buckets/{bucket}/uploads/{uploadId}/parts/{partNumber}?key=` | raw bytes (≤ 5 GiB) | `200 { "partNumber", "eTag" }` | 400, 404 `UPLOAD_NOT_FOUND` |
| POST | `/api/v1/buckets/{bucket}/uploads/{uploadId}/complete?key=` | `{ "parts": [{ "partNumber", "eTag" }] }` | `200 ObjectWrittenResponse` (`md5-N` ETag) | 400 `UPLOAD_INVALID_PART`, `UPLOAD_PART_TOO_SMALL`, `UPLOAD_INVALID_PART_ORDER`, 404 |
| DELETE | `/api/v1/buckets/{bucket}/uploads/{uploadId}?key=` | — | `204` | 404 |
| GET | `/api/v1/buckets/{bucket}/objects/versions?prefix=&keyMarker=&versionIdMarker=&limit=` | — | `200 ObjectVersionListResponse` | 400 `OBJECT_INVALID_VERSION_ID`, 404 |
| POST | `/api/v1/buckets/{bucket}/objects/restore` | `{ "key", "versionId" }` | `200 ObjectWrittenResponse` (the version copied over its key, as the new latest) | 400, 404 `OBJECT_NO_SUCH_VERSION` |
| GET | `/api/v1/buckets/{bucket}/objects/access?key=&versionId=` | — | `200 { "url", "isPublic" }`: the object's URL on the S3 port; `isPublic` when the bucket policy lets anyone read it without conditions | 400, 404 |
| POST | `/api/v1/buckets/{bucket}/objects/share` | `{ "key", "versionId"?, "expiresInSeconds" }` (1–604 800) | `200 { "url", "expiresAt", "signedBy" }`, `Cache-Control: no-store`: a presigned GET URL signed with the signed-in key | 400 `OBJECT_INVALID_SHARE_EXPIRY`, `OBJECT_INVALID_VERSION_ID`, 404 |

Details and downloads take `&versionId=` (the latest without it). The delete request also takes
`"versions": [{ "key", "versionId" }]`: those versions or delete markers are removed permanently; `keys` deletes the
latest (a delete marker in a versioned bucket). `ObjectVersionListResponse`: `{ prefix, folders, versions: [{ key,
versionId, isLatest, isDeleteMarker, size, eTag, lastModified }], isTruncated, nextKeyMarker, nextVersionIdMarker }`.

- `ObjectListResponse`: `{ "prefix", "folders": [{ "prefix" }], "objects": [{ "key", "size", "eTag", "lastModified", "versionId" }], "isTruncated", "nextStartAfter" }`.
  The folder's own marker object (`key == prefix`) is not listed.
- `ObjectDetailsResponse`: key, size, eTag, contentType, lastModified, versionId, checksumAlgorithm, checksum
  (base64), checksumType, partsCount (multipart objects), headers and userMetadata (`[{ "name", "value" }]`).
- A zero-byte object named `folder/` is a folder marker (what "New folder" writes).
- URLs use `TimeStore:S3:PublicUrl` when set, else the host the admin API was called on with `S3Port`. A share link
  is a bearer credential for one object: it is returned once, never stored or logged, and stops working when it
  expires or when the key that signed it is disabled or deleted (the root key's links: only by rotating its secret).

## Metrics

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/metrics` | — | `200 MetricsResponse` (viewers too) | 401 |

`MetricsResponse`: `startedAt`, `uptimeSeconds`, `storage` (buckets, objects, totalBytes, noncurrentVersions,
noncurrentBytes, incompleteUploads,
incompleteUploadBytes, diskTotalBytes, diskFreeBytes, `byBucket: [{ name, objects, totalBytes }]`, retainedBlobs and retainedBytes: released blobs still on disk, waiting for `Blobs:DeleteDelaySeconds` or `Blobs:RetainDeletedDays`), `replication` (every rule as `ReplicationResponse`, backlog without the failure list), `requests` since
start on the S3 port (total, reads, writes, lists, deletes, other, clientErrors, serverErrors, bytesIn, bytesOut,
p50Ms, p99Ms), `sampleIntervalSeconds` (10) and `samples` (up to 90, oldest first: `at`, requests, clientErrors,
serverErrors, bytesIn, bytesOut, p50Ms, p99Ms within the interval; objects and totalBytes at its end).

- Request classes follow Amazon S3 request metrics: reads = GET/HEAD object, HEAD bucket, location, versioning;
  writes = PUT, copy, multipart create/part/complete, create bucket; lists; deletes = object(s), bucket, abort.
- Latency percentiles are the upper bound of the histogram bucket (1, 2, 5, 10, 25, 50, 100, 250, 500 ms, 1, 2.5, 5,
  10 s). Bytes out count responses with a known length. Counters live in memory and restart at zero.

## Access keys

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/access-keys` | — | `200 AccessKeyResponse[]` (creation order) | — |
| POST | `/api/v1/access-keys` | `{ "description"?, "role"?, "s3Scope"? }` | `201 CreatedAccessKeyResponse`, `Cache-Control: no-store` | 400 `ACCESS_KEY_INVALID_DESCRIPTION`, `ACCESS_KEY_INVALID_ROLE`, `ACCESS_KEY_INVALID_SCOPE` |
| POST | `/api/v1/access-keys/{id}/role` | `{ "role" }` | `200 AccessKeyResponse` (its sessions end) | 400 `ACCESS_KEY_INVALID_ROLE`, 404 `ACCESS_KEY_NOT_FOUND` |
| POST | `/api/v1/access-keys/{id}/scope` | `{ "s3Scope": "full" \| "policy" }` | `200 AccessKeyResponse` (applies from the key's next S3 request) | 400 `ACCESS_KEY_INVALID_SCOPE`, 404 `ACCESS_KEY_NOT_FOUND` |
| POST | `/api/v1/access-keys/{id}/disable` | — | `200 AccessKeyResponse` | 404 `ACCESS_KEY_NOT_FOUND` |
| POST | `/api/v1/access-keys/{id}/enable` | — | `200 AccessKeyResponse` | 404 `ACCESS_KEY_NOT_FOUND` |
| DELETE | `/api/v1/access-keys/{id}` | — | `204` | 404 `ACCESS_KEY_NOT_FOUND` |

`AccessKeyResponse`: `{ "accessKeyId": "TS…", "description": "ci", "role": "none", "createdAt": "…Z", "disabledAt": null, "s3Scope": "full" }`.
`s3Scope`: `full` (default; every bucket unless a bucket policy denies) or `policy` (only what bucket policies allow;
no ListBuckets/CreateBucket).
Roles: `none` (default: S3 only), `viewer`, `admin`.

`CreatedAccessKeyResponse`: `{ "accessKeyId", "secretKey", "description", "role", "createdAt", "s3Scope" }`. The **only** response
that carries a secret; it cannot be read again (stored encrypted). A disabled or deleted key fails S3 requests with
`InvalidAccessKeyId` from the next request on. The root key from configuration is not listed and answers 404 here.

## Backups

An API for **programs**, not for the admin UI. Every request is signed with AWS Signature Version 4 in the
`Authorization` header, exactly like an S3 request (`curl --aws-sigv4 "aws:amz:<region>:s3"`, the AWS SDKs), by an
access key with the `backup` or `admin` role; reads included, since an archive is the whole server. Design:
`docs/research/backup.md`; operators: `docs/backup.md`.

- No signature (a sign-in cookie, an unsigned request): 401 `AUTH_SIGNATURE_REQUIRED`. Cookies are never accepted
  here, so a browser cannot run backups; there is no CSRF check (nothing to forge without the secret key).
- A bad signature, an unknown or disabled key, a request too old: 403 with the S3 authentication codes
  (`S3_AUTH_SIGNATURE_MISMATCH`, `S3_AUTH_INVALID_ACCESS_KEY`, `S3_AUTH_TIME_SKEWED`, …).
- A key whose role is `none` or `viewer`: 403 `AUTH_FORBIDDEN`.
- The body (the start request's JSON) must match `x-amz-content-sha256`, or be sent as `UNSIGNED-PAYLOAD`;
  `aws-chunked` bodies and bodies over 64 KiB are refused: 400 `AUTH_PAYLOAD_MISMATCH`.
- A `backup` key reaches nothing else: it cannot sign in, and the rest of `/api/v1` needs a session.

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| POST | `/api/v1/backups` | `{ "target"?: "directory", "compression"?: "zstd" \| "none", "content"?: "full" \| "metadata" }` | `202 BackupRunResponse` (queued), `Location` | 400 `BACKUP_INVALID_TARGET`, `BACKUP_INVALID_COMPRESSION`, `BACKUP_INVALID_CONTENT`, `BACKUP_RETENTION_REQUIRED` (metadata only while `Blobs:RetainDeletedDays` is 0), `BACKUP_TARGET_NOT_CONFIGURED`; 409 `BACKUP_ACTIVE` |
| GET | `/api/v1/backups` | — | `200 BackupRunResponse[]` (newest first, at most 100) | — |
| GET | `/api/v1/backups/{id}` | — | `200 BackupRunResponse` | 404 `BACKUP_NOT_FOUND` |
| POST | `/api/v1/backups/{id}/cancel` | — | `200 BackupRunResponse` (queued: cancelled at once; running: stops at its next blob, poll for the end) | 404, 409 `BACKUP_NOT_ACTIVE` |
| GET | `/api/v1/backups/{id}/archive` | — | `200` the archive (`application/zstd` or `application/x-tar`, `Content-Disposition: attachment`, `no-store`) | 404 `BACKUP_NOT_FOUND`, `BACKUP_ARCHIVE_NOT_FOUND` |
| DELETE | `/api/v1/backups/{id}/archive` | — | `204` the archive is deleted from the backup folder; the run stays | 404 `BACKUP_NOT_FOUND`, `BACKUP_ARCHIVE_NOT_FOUND` |
| GET | `/api/v1/backups/stream?compression=&content=` | — | `200` the archive, written while the backup runs; nothing is kept on the server | 400 `BACKUP_INVALID_COMPRESSION`, 409 `BACKUP_ACTIVE` |

`BackupRunResponse`: `{ "id", "status", "trigger", "target", "compression", "startedBy",
"queuedAt", "startedAt"?, "finishedAt"?, "snapshotAt"?, "blobsTotal", "blobsDone", "bytesTotal", "bytesDone",
"archiveName"?, "archiveSize"?, "archiveSha256"?, "errorCode"?, "errorMessage"?, "content" }` (`full` or `metadata`:
the metadata only, no blob; docs/backup.md "Metadata-only backups").

- **Status:** `queued` → `running` → `succeeded` | `failed` | `cancelled` | `interrupted` (the server stopped or the
  streaming client disconnected; never re-run automatically). At most one run is queued or running.
- **Trigger:** `manual` (POST), `api` (stream), `schedule` (later). **Target:** `directory` (`TimeStore:Backup:Path`, default `<DataPath>/backups`; the newest `Backup:Keep` archives stay)
  or `stream`.
- **Progress** (`blobsDone`, `bytesDone`) is written every 2 s; `snapshotAt` is instant T, what the archive holds.
- **Stream:** refusals are JSON before the first byte. A failure after it cuts the connection, so a client never
  receives a truncated archive as a whole one (and a restore checks `blobs.sha256`, the last entry, anyway).
- `errorCode` of a run that did not succeed: `BACKUP_BLOB_MISSING`, `BACKUP_BLOB_CORRUPT` (a blob does not match its
  stored MD5 or size), `BACKUP_LOW_DISK_SPACE` (`Backup:MinFreeBytes`, or no room for an archive as large as the data), `BACKUP_CANCELLED`, `BACKUP_INTERRUPTED`,
  `BACKUP_TARGET_NOT_CONFIGURED` (the run's target is not available after a restart), `BACKUP_FAILED` (details in
  the server log).
- Every start, cancel, download and outcome is logged with the access key, never data or keys.

The `restore` command (offline, `docs/backup.md`) reports `BACKUP_RESTORE_NOT_EMPTY`, `BACKUP_RESTORE_INVALID_ARCHIVE`,
`BACKUP_RESTORE_UNSUPPORTED` (newer archive format or schema), `BACKUP_RESTORE_CHECKSUM_MISMATCH` and
`BACKUP_RESTORE_ARCHIVE_UNAVAILABLE` (a missing file, a URL that does not answer 2xx). Archives never hold the master
key: the restored server uses the key of its configuration.

## Replication

A bucket's replication rule (R1, design: `docs/research/replication.md`; guide: `docs/user-guide/replication.md`).
Viewers read, admins change. The destination's secret key is accepted and never returned.

| Method | Path | |
|---|---|---|
| GET | `/api/v1/buckets/{name}/replication` | The rule and its backlog (`ReplicationResponse`); 404 `REPLICATION_NOT_FOUND` |
| PUT | `/api/v1/buckets/{name}/replication` | Create or replace (`SetReplicationRequest`). Writes and deletes a test object at the destination first; a refusal is 409 `REPLICATION_DESTINATION_FAILED` with the destination's S3 error code. `existingObjects` (default true) queues every current object in the same transaction, for a new rule or a new endpoint, bucket or prefix (not for other edits) |
| DELETE | `/api/v1/buckets/{name}/replication` | 204; what waits in the queue is dropped, nothing is deleted at the destination |
| POST | `/api/v1/buckets/{name}/replication/test` | Writes and deletes a test object with the saved settings |
| POST | `/api/v1/buckets/{name}/replication/retry` | Failed changes are sent again; a paused rule resumes now |
| POST | `/api/v1/buckets/{name}/replication/resync` | Every current object under the prefix is queued again (a destination that lost data or was replaced) |

`SetReplicationRequest`: `endpoint` (`http(s)://host[:port]`, no path), `bucket`, `region` (default `us-east-1`; R2:
`auto`), `accessKeyId`, `secretKey` (required for a new rule or a new endpoint or key; omitted keeps the stored one),
`prefix`, `replicateDeletes` (default false), `existingObjects` (default true).

`ReplicationResponse`: the settings, `pausedUntil` and `lastError` (temporary destination failures), `lastSuccessAt`,
and `backlog`: `pending`, `oldestPendingAt` (the lag), `failed`, `failures` (newest 100: key, operation, attempts,
error).

Errors: 400 `REPLICATION_INVALID_ENDPOINT`, `REPLICATION_INVALID_DESTINATION`, `REPLICATION_SECRET_REQUIRED`; 404
`BUCKET_NOT_FOUND`, `REPLICATION_NOT_FOUND`; 409 `REPLICATION_DESTINATION_FAILED`, `REPLICATION_DISABLED`
(`Replication:Enabled=false`).

## Maintenance

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| POST | `/api/v1/maintenance/blob-sweep` | — | `200 BlobSweepResponse` | 409 `MAINTENANCE_SWEEP_RUNNING` |

`BlobSweepResponse`: `{ "scanned": 2, "deleted": 1, "deletedBytes": 4, "startedAt": "…Z", "finishedAt": "…Z" }`.
`scanned` counts blob files older than the grace period (`TimeStore:BlobSweep:GracePeriodMinutes`); `deleted` those
no object references. Files in `blobs/` that are not blobs by name and shard are never touched. The same sweep runs
in the background at startup and every `TimeStore:BlobSweep:IntervalMinutes`.

## Common errors

| Code | Status | When |
|---|---|---|
| `COMMON_INVALID_REQUEST` | 400 | Malformed or missing body |
| `INTERNAL_ERROR` | 500 | Unexpected failure; details only in the log (same `traceId`) |
