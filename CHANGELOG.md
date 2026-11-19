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

- One Native AOT binary (Linux x64 and arm64, macOS arm64) and a Linux container image (`deploy/Dockerfile`).
- Metadata in SQLite (WAL, `synchronous=FULL`) with a single writer and group commit; blobs as immutable files on
  local disk. Every write is on disk (fsync) before the server answers.
- Secret access keys are stored encrypted with a master key (`master.key` in the data directory).
- Benchmarks against other S3 servers: `docs/performance.md`.
- Built-in backups of a running server (beta: the API and archive format may change before 1.0): one consistent archive at an instant (tar + Zstandard or plain tar, every
  blob checked against its MD5, a manifest and a `blobs.sha256` list), streamed from `GET /api/v1/backups/stream` or
  written to `Backup:Path` by a background worker; runs with history, progress and cancel; `TimeStore.Server restore`
  checks every entry and restores into an empty data directory. The master key is only included when an admin key asks
  for it. The backup API takes requests signed with SigV4 (like S3, `curl --aws-sigv4`) by a key with the new
  **`backup`** role (backup API only, no admin UI) or an admin key; sign-in cookies are refused. Restores read the
  archive from a file or an http(s) URL; `--if-empty` and the Helm chart's `restore.archive` (an init container)
  restore an empty volume before the server starts and never touch one with data.
- Backup and restore guide: `docs/backup.md`.
- Helm chart for Kubernetes (`deploy/helm/timestore`, published as `oci://ghcr.io/tainguyen1501/charts/timestore`),
  with a backups claim (`backup.existingClaim`) and restore steps (a Job, in place or side by side).

### Fixed

- S3 `PutObject` and `UploadPart` bodies over 30 MB (Kestrel's default limit) failed with 500; they now take up to
  5 GiB as S3 allows (rclone sends files up to 200 MiB in one PutObject). A body Kestrel refuses (too large, sent too
  slowly) is answered with the S3 error (`MaxMessageLengthExceeded`, `RequestTimeout`), not `InternalError`.

### Known limits

- Single node only; replication across nodes is planned (`docs/roadmap.md`, phase 8).
- No server-side encryption, object lock, object tags, bucket replication, or IAM users and groups.
- Lifecycle rules cannot transition objects to other storage classes (there is only `STANDARD`).
- The admin port should stay on loopback or behind a TLS proxy (`AdminAddress`, `Admin:SecureCookies`;
  `docs/configuration.md`).
