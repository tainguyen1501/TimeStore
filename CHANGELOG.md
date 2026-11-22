# Changelog

All notable changes to TimeStore are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html); before 1.0, a minor version (0.x.0) may contain breaking
changes, and they are always listed under **Changed** or **Removed** with what to do. How a release is made:
`CONTRIBUTING.md`, "Releases".

Sections per version: **Added**, **Changed**, **Deprecated**, **Removed**, **Fixed**, **Security**.

## [Unreleased]

The first public release, 0.1.0. A single-node, S3-compatible object storage server.

### Added

**S3 API** (port 9000)

- Path-style and virtual-host addressing (`S3:Domains`), AWS Signature Version 4 in headers and presigned URLs,
  `aws-chunked` uploads in all three modes, errors and XML as Amazon S3 returns them. Tested with the AWS SDKs, the
  AWS CLI, rclone and s3cmd, a subset of ceph s3-tests and the applicable cases of the official SigV4 test suite.
- Buckets: create, list, head, delete (only when empty), location, versioning, lifecycle rules, bucket policies.
- Objects: put, get with ranges and conditional requests, head, delete, multi-delete, copy, user metadata, listings
  v1 and v2 (prefix, delimiter, continuation) and object version listings.
- Checksums: `Content-MD5` and the `x-amz-checksum-*` algorithms (CRC32, CRC32C, CRC64NVME, SHA-1, SHA-256), stored
  and returned.
- Multipart uploads: create, upload part, upload part copy, complete (composite and full-object checksums), abort,
  list parts and uploads, GET/HEAD by `partNumber`; abandoned uploads are aborted after `Multipart:AbandonAfterDays`.
- Versioning with delete markers; suspended buckets keep a `null` version.
- Lifecycle rules: expiration of current and noncurrent versions, expired delete markers, incomplete multipart
  uploads.
- Bucket policies (IAM policy language: principals, actions, resources, conditions) and anonymous access when a
  policy allows it. Access keys can be limited to what bucket policies grant.
- Requests for features that do not exist yet (server-side encryption, object lock, tags, storage classes other than
  `STANDARD`) are refused with `NotImplemented`, never silently ignored.

**Admin API and UI** (port 9001, loopback by default)

- Sign-in with an access key and its secret; roles `viewer` and `admin`; server-side sessions.
- Access keys: create (the secret is shown once), disable, delete, roles and S3 scope.
- Buckets and objects: create and delete buckets, browse folders (list, grid and table views, virtualized for large
  folders), upload, download, create folders, delete objects and folders, preview images, video, audio and text.
- Object versions (show, restore, delete), versioning on and suspend, lifecycle rules, bucket policy editor, make a
  bucket or folder public or private, share links (presigned, up to 7 days).
- Dashboard and metrics (requests, traffic, latency, storage per bucket), a user guide inside the UI, light and dark
  themes.
- The UI is embedded in the server binary; OpenAPI document for the admin API.

**Storage and operations**

- Metadata-only backups (`"content": "metadata"`): the metadata alone, in seconds, restorable onto the same data
  directory while `Blobs:RetainDeletedDays` keeps the blobs it references (`restore --metadata`, which checks every
  blob first; Helm `restore.metadataArchive`). The server now holds `<DataPath>/.timestore-lock`: a second server on
  the same directory refuses to start.
- Released blobs (overwritten, deleted, replaced or aborted parts) are recorded in the metadata by the commit that
  releases them and deleted by a collector, instead of an in-memory queue: a crash forgets nothing. New
  `Blobs:RetainDeletedDays` (default 0) keeps them longer so older metadata backups stay restorable, and
  `Blobs:CollectSeconds`; metrics show `retainedBlobs` / `retainedBytes`.
- Bucket replication (R1): a per-bucket rule (`/api/v1/buckets/{name}/replication`) sends every change, and the
  objects already there, to another S3 endpoint within seconds; changes are queued in the write's own transaction (no
  loss on a crash), coalesced per key, retried with a backoff while the destination is down, and checked with
  `Content-MD5`. Set up and watched in the admin UI (bucket page → **Replication**: test, status, refused changes,
  retry, resend everything, remove); rules and backlogs in the metrics. Settings `TimeStore:Replication:*`
  (`docs/user-guide/replication.md`).
- One Native AOT binary (Linux x64 and arm64, macOS arm64) and a Linux container image (`deploy/Dockerfile`).
- Metadata in SQLite (WAL, `synchronous=FULL`) with a single writer and group commit; blobs as immutable files on
  local disk. Every write is on disk (fsync) before the server answers.
- Secret access keys are stored encrypted with a master key you provide (`TimeStore:MasterKey`, or
  `TimeStore:MasterKeyFile`); the server never creates one and refuses to start with a key that does not decrypt
  the stored access keys.
- Benchmarks against other S3 servers: `docs/performance.md`.
- Built-in backups of a running server (beta: the API and archive format may change before 1.0): one consistent archive at an instant (tar + Zstandard or plain tar, every
  blob checked against its MD5, a manifest and a `blobs.sha256` list), streamed from `GET /api/v1/backups/stream` or
  written to `Backup:Path` by a background worker; runs with history, progress and cancel; `TimeStore.Server restore`
  checks every entry and restores into an empty data directory. Archives never hold the master key (it is configuration). The backup API takes requests signed with SigV4 (like S3, `curl --aws-sigv4`) by a key with the new
  **`backup`** role (backup API only, no admin UI) or an admin key; sign-in cookies are refused. Restores read the
  archive from a file or an http(s) URL; `--if-empty` and the Helm chart's `restore.archive` (an init container)
  restore an empty volume before the server starts and never touch one with data.
- Backup and restore guide: `docs/backup.md`.
- Helm chart for Kubernetes (`deploy/helm/timestore`, published as `oci://ghcr.io/tainguyen1501/charts/timestore`),
  with backups in the data volume (`backup.keep`) and restores through `restore.archive` (in place, side by side, a
  new cluster) or the example manifests.

### Changed

- **Backups go to `<DataPath>/backups` by default** (`TimeStore:Backup:Path` moves them; a folder of its own, never the
  data directory, `blobs/` or `tmp/`): the folder is where archives are taken from. `TimeStore:Backup:Keep` (default 1)
  keeps the newest archives and deletes older ones after a backup succeeds; `DELETE /api/v1/backups/{id}/archive`
  frees an archive once taken; a backup needs room for an archive as large as the data and is refused otherwise.
  Helm: `backup.existingClaim` and its `/backups` mount are removed (archives are in the data volume), `backup.keep`
  is new, and `restore.archive` takes an http(s) URL; `examples/backups-claim.yaml` is gone and `restore-job.yaml`
  reads a URL.
- **The master key is configuration, required** (`TimeStore:MasterKey` as Base64, or `TimeStore:MasterKeyFile`):
  TimeStore no longer creates `master.key` in the data directory, and a start with a key that does not decrypt the
  stored access keys is refused. Backups never carry the key: `includeMasterKey` and `BACKUP_MASTER_KEY_FORBIDDEN`
  are gone, the restore command has no `--master-key`; archives of earlier versions that hold a key still restore (the
  key is written to `master.key` in the data directory). Helm: `masterKey.value` or `masterKey.existingSecret` is
  required. **Upgrading** a server that has `<data>/master.key`: move the file out of the data directory and set
  `TimeStore:MasterKeyFile` to it, or set `TimeStore:MasterKey` to `base64 < master.key` (Helm: a Secret with it, or
  `masterKey.value`). The server names the file in its startup error until then.

### Fixed

- S3 `PutObject` and `UploadPart` bodies over 30 MB (Kestrel's default limit) failed with 500; they now take up to
  5 GiB as S3 allows (rclone sends files up to 200 MiB in one PutObject). A body Kestrel refuses (too large, sent too
  slowly) is answered with the S3 error (`MaxMessageLengthExceeded`, `RequestTimeout`), not `InternalError`.

### Known limits

- Single node only; replication across nodes is planned (`docs/roadmap.md`, phase 8).
- No server-side encryption, object lock, object tags, or IAM users and groups. Bucket replication copies only the
  current state of each key, not every version.
- Lifecycle rules cannot transition objects to other storage classes (there is only `STANDARD`).
- The admin port should stay on loopback or behind a TLS proxy (`AdminAddress`, `Admin:SecureCookies`;
  `docs/configuration.md`).
