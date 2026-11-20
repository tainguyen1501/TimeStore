# TimeStore roadmap

Status legend: ✅ done · 🚧 in progress · ⏳ planned · ⏸ deferred.

## Phases

| # | Phase | Status | Content |
|---|---|---|---|
| 0 | Skeleton | ✅ | Solution layout, conventions + rules, Primitives, ports, SQLite + file-system providers, Server with two ports, health, admin bucket API, contract + convention tests. React shell + Buckets page. Verified on SDK 11.0.100 (2026-11-16): build 0 warnings, 38/38 tests, Runtime Async active, Native AOT publish (osx-arm64) without IL warnings: 10.9 MB binary + `libe_sqlite3.dylib`, ~26 MB RSS idle |
| 1 | Protocol | ✅ | S3 routing (path + virtual-host), operation `closed record` hierarchy, SigV4 header + presigned, `aws-chunked`, XML writers/readers, error map |
| 2 | Single-node MVP | ✅ 2a–2g + admin sign-in (`docs/research/admin-auth.md`) | Buckets over S3; Put/Get/Head/Delete object; ListObjectsV2 (prefix, delimiter, continuation); ranges (pooled-buffer copy: Kestrel has no kernel sendfile); ETag MD5; access keys + admin sign-in (then the admin port may leave loopback). Carry-overs: fsync the blob directory after rename (create shard directories once, not per write); `DeleteBucket` only when empty (`BUCKET_NOT_EMPTY`, atomic in the store + contract fact) |
| 3 | Multipart | ✅ 3a–3f (`docs/research/s3-multipart.md`); UploadPartCopy moves to phase 6 (CopyObject: phase 4) | Create/UploadPart/Complete/Abort/ListParts/ListMultipartUploads, `md5-N` ETags, orphan-part GC |
| 4 | Compatibility | ✅ 4a–4e (`docs/research/s3-compatibility.md`): opt-in harness (AWS CLI, rclone, s3cmd), unsupported headers refused, CopyObject, ceph s3-tests ratchet (200 tests), official SigV4 suite (32 applicable cases) | Opt-in compatibility harness (AWS CLI, rclone, s3cmd; mc dropped: no public image), reject unsupported write headers, **CopyObject** (moved from phase 6), ceph/s3-tests subset with a ratchet list, applicable cases of the official `aws-sig-v4-test-suite` (downloaded at test time, opt-in, never committed) |
| 5 | Performance | ✅ (benchmark harness; single writer + group commit: Linux small PUTs 3.3x, p99 4x lower; delayed blob deletion; Server GC + DATAS and Runtime Async kept on numbers; SigV4 allocations measured and left as they are) (`docs/research/performance.md`, `docs/performance.md`) | ~~Delayed deletion of replaced blobs~~ (done: 60 s grace), ~~single writer + group commit~~ (done), ~~pooling audit~~ (done: reads run no pragmas, the writer applies them once), ~~Runtime Async/GC measurements~~ (done: defaults kept, stated in the server project), ~~`warp` baseline~~ (done). ~~SigV4/aws-chunked hot path~~ (measured in 5d: under 0.3% of a PUT's CPU budget and the GC is not a limit, so no rewrite; reopen if a CPU-bound profile shows it) |
| 6 | Features | ✅ 6a (versioning + delete markers, `docs/research/s3-versioning.md`), 6b (UploadPartCopy), 6c (lifecycle, `docs/research/s3-lifecycle.md`), 6d (bucket policies, key S3 scopes, anonymous access, `docs/research/s3-bucket-policy.md`) | Versioning + delete markers, UploadPartCopy, lifecycle, bucket policy (conditional requests and user metadata moved to phase 2) |
| 7 | Admin UI | ✅ 7a ✅ (sign-in), 7b ✅ (OpenAPI + generated types, UI embedded and served on the admin port), 7c ✅ (access keys page, LRU signing-key cache), 7d ✅ (object browser), 7e ✅ (metrics), 7f ✅ (preview, S3 URL with public state, share links), 7g ✅ (Make public / private for a bucket or folder), 7h ✅ (virtualized object list, scroll loading, name filter), 7i ✅ (redesign: three-column shell, borderless tonal style, list / grid / table views, preview window, Guide and Settings pages) (`docs/research/admin-ui.md`) | 7a sign-in page, guard, session expiry, user menu, viewer read-only UI; 7b OpenAPI document + generated types, serve the build from the Server (static files + SPA fallback); 7c access keys page; 7d object browser; 7e metrics; 7f object preview and links; 7g make public; 7h large folders; 7i redesign |
| 8 | Scale-out | ⏸ deferred; phases 6 and 7 are done; start when the maintainers decide | Sharded SQLite, Postgres provider, `IPlacement` + replication (model 2), node membership, rebalancing. Erasure coding is not in phase 8: replicas first, EC for cold data later (Backlog) |

## Data protection

Overview for users: [data-protection.md](data-protection.md). Four mechanisms; backups and replication have their own
stages.

| Mechanism | Stage | Status | Content |
|---|---|---|---|
| Versioning | (phase 6a) | ✅ | Versions and delete markers; restore an older version in the admin UI; lifecycle expires old versions |
| Mirroring | — | ✅ documented | No TimeStore code: `rclone sync` / `copy` to any storage, as a CronJob ([mirroring.md](mirroring.md)) |
| Backups (beta until 1.0) | B1 | ✅ | Point-in-time archive (tar + Zstandard), blob hold, `backup_runs`, targets `stream` and `directory` (`<DataPath>/backups`, newest `Backup:Keep` kept), admin API, `restore` command, Helm `restore.archive` and restore manifests ([research/backup.md](research/backup.md)) |
| | B1.1 | ✅ | Restore from a URL or S3 (`restore --archive https://…`), `--if-empty`, and a declarative `restore.archive` in the Helm chart (an init container): a restore becomes Values → Plan → Apply on TimeOps |
| | B2 | ⏳ | Backups page in the admin UI |
| | B3 | ⏳ | Schedules and retention |
| | B4 | ⏳ | `s3` target: archives sent to another S3 endpoint while they are written |
| | B5a | ✅ built (2026-11-20, uncommitted for review) | Released blobs kept in `pending_blob_deletions` (written in the commit's transaction) and deleted by a collector after `Blobs:RetainDeletedDays` (default 0: as today); retained-bytes metric ([research/blob-retention.md](research/blob-retention.md)) |
| | B5b | ⏳ | Metadata-only backups (`content: metadata`, seconds, refused at retention 0) and `restore --metadata` with a check of every referenced blob |
| | B5c | ⏳ | Incremental blob copy to an S3 endpoint (blobs never change: only new ones are sent); until then `rclone copy` of `blobs/` |
| | B6 | ⏳ | Archive encryption |
| Replication | R1 | ✅ built (2026-11-19, uncommitted for review) | Per-bucket rule to one S3 destination (admin API, tested on save), outbox in the five write methods' transaction, worker (coalescing, temporary vs permanent failures, Content-MD5 + ETag check, source parts kept), existing objects queued with the rule, own SigV4 client (Native AOT) ([research/replication.md](research/replication.md), "R1 as built") |
| | R2a | ✅ built (2026-11-20, uncommitted for review) | Admin UI: Replication sheet on the bucket page (set up with a test, status, refused changes, retry, resync, remove); `POST …/replication/resync`; rules and backlogs in `/api/v1/metrics` |
| | R2b | ⏳ | `x-amz-replication-status` on GET/HEAD (hot path: research and measure first), bandwidth limit |
| | R3 | ⏳ | Versioned buckets (every version), S3 `PutBucketReplication` API |
| | R4 | ⏳ | Two-way / site replication between TimeStore servers |

Order: B1.1 done; replication R1, R2a and B5a (blob retention) built; next B5b (metadata-only backups) or R2b. Open question (proposed 2026-11-19, not decided): schedules, destinations and
a backups page (B2–B4) in a backup tool outside the server (TimeOps, a CronJob) that calls the stream API, the server
keeping only the consistent snapshot (stream) and the restore, as Vault and etcd do.

## Phase 1 approach

Research before code: for routing, SigV4, `aws-chunked` and XML/errors, read the AWS spec; write
`docs/research/s3-protocol.md`; decide the C# design per piece; then fix the phase 1 steps below and code.

Research done and approved: `docs/research/s3-protocol.md`. Steps: 0 ✅ `TimeStore.Cryptography`
(checksums via `System.IO.Hashing` 11, HMAC) + `ICacheService` on `IMemoryCache` (HybridCache fails AOT), 1a ✅ routing, 1b ✅ SigV4 middleware
(header + presigned, root key via `ICredentialStore`), 1c ✅ body middleware: `aws-chunked` in all three modes +
checksums (needed by current AWS SDK defaults), 1d ✅ response writer + error map + XML, 1e ✅ bucket operations over S3 (end-to-end tests with the AWS SDK, also against the AOT binary).
Done in 1d: convention test that every catalog code has an `S3ErrorMap` entry; draining is not needed (Kestrel
does it, verified). Done in phase 2: `Content-MD5` verification (PutObject,
DeleteObjects) and returning stored checksums. `SigningKeyCache` became a bounded LRU in step 7c.

## Before public release

- Done: reference notes and local paths removed; other S3 servers are named only in the benchmark material
  (`deploy/bench/compare.sh`, `docs/benchmarks/`, `docs/performance.md`, `docs/research/performance.md` §11, the
  performance rows of the backlog).
- Done: `LICENSE` (AGPL-3.0; the maintainers keep the right to offer a commercial license, so contributions need a CLA).
- Done: `CONTRIBUTING.md`, `CLA.md` (based on the Apache ICLA, allows other licenses) and the CLA bot
  (`.github/workflows/cla.yml`).
- Done: `SECURITY.md` (GitHub private vulnerability reporting; enable it in the repository settings once public).
- Done: `CHANGELOG.md` (0.1.0 in `[Unreleased]` until tagged) and versions from tags only (`vX.Y.Z`, local builds
  `0.0.0-dev`; `CONTRIBUTING.md`, "Releases").
- Done: CI (`.github/workflows/ci.yml`: backend build + tests; frontend lint, typecheck, generated API types, build;
  Native AOT publish for linux-x64 with the UI embedded + end-to-end tests against the binary) and
  `.github/dependabot.yml` (monthly, grouped, minor and patch only).
- Done: release workflow (`.github/workflows/release.yml`) on `v*` tags: tag format, on `main`, changelog section;
  Native AOT binaries (linux-x64, linux-arm64, osx-arm64) tested end to end; multi-arch image on ghcr.io; GitHub
  Release with archives and `SHA256SUMS`. The container build now embeds the user guide (it was missing:
  `.dockerignore` excluded `docs/`).
- Done: README for the public release (why, quick start, what works, benchmarks on Linux and macOS with the method
  and caveats); settings moved to `docs/configuration.md`.
- Done: Helm chart (`deploy/helm/timestore`): one-replica StatefulSet with its volume, generated or existing root
  keys, required master key (a value or a Secret), S3 and admin services and ingresses, non-root read-only pod, `helm test`; CI lints,
  renders and validates it; releases push it to `oci://ghcr.io/<owner>/charts`. Verified on k3s 1.31 (install,
  `helm test`, ingress, upgrade, pod restart, uninstall and reinstall keep the data; Helm 3.16 and 4).
- Left: a release candidate (`v0.1.0-rc.1`); `v0.1.0`; make the repository
  public; rulesets for `main` and `cla-signatures`; private vulnerability reporting; ghcr package visibility public.
- Done: benchmark comparison on a 4-vCPU Linux VPS (`docs/benchmarks/2026-11-17-compare-linux-4vcpu.md`). Optional: a
  run on a dedicated 8-vCPU machine, where warp is not the limit.
- Done: fresh repository with a single initial commit (the private history stays in a separate private repository).

## Multi-node: decisions to keep now (cheap now, expensive later)

1. Blobs are immutable; overwrite = new blob + metadata pointer move.
2. Metadata stores a `BlobLocator` (id, node, size, checksum), never a path.
3. IDs are UUIDv7 generated anywhere; no autoincrement.
4. Metadata writes are atomic/compare-and-swap in the store; correctness never relies on in-process locks.
5. The HTTP layer is stateless (multipart state, IAM in the store).
6. Every blob has a checksum (scrub/repair later).
7. `NodeId` is configured from day one.
8. Background jobs run under a lease (`ILeaseProvider`, in-process for single node).
9. Placement goes through `IPlacement` (single node: local).

Items 4, 8 and 9 get their interfaces when the first code needs them (phases 2–5).

10. Backups (`docs/research/backup.md`; B1 done, B2–B6 planned): on one node a backup is a `backup_runs` row (at most
    one active, a partial unique index); with several nodes it runs under a lease (owner + expiry, item 8), holds blobs
    on every node holding a replica and snapshots the shared metadata store. The engine (`BackupRunner`) takes the hold
    (`IBlobHold`), snapshot source (`IMetadataSnapshots`), blob reader (`IBlobStore`) and target (`IBackupTarget`) as
    interfaces for that reason.

## S3 operations

| Operation | Status |
|---|---|
| ListBuckets, CreateBucket, DeleteBucket, HeadBucket, GetBucketLocation, GetBucketVersioning | ✅ |
| PutObject (incl. `If-None-Match: *`, Content-MD5, checksums, user metadata) | ✅ |
| GetObject, HeadObject (ranges, conditional requests, response overrides, checksum mode) | ✅ |
| DeleteObject, DeleteObjects (quiet, Content-MD5 or checksum required, batched in one transaction) | ✅ |
| ListObjectsV2, ListObjects (v1) (skip-scan delimiters, base64url tokens, `encoding-type=url`, `fetch-owner`) | ✅ |
| Multipart: Create, UploadPart, Complete (composite checksums), Abort, ListParts, ListMultipartUploads | ✅ |
| Multipart: GET/HEAD of multipart objects (ranges across parts, `partNumber`, `x-amz-mp-parts-count`) | ✅ |
| Multipart checksums: `COMPOSITE` and `FULL_OBJECT` (CRC32/CRC32C/CRC64NVME combined from part CRCs) | ✅ |
| CopyObject (COPY/REPLACE metadata, `x-amz-copy-source-if-*`, cross-bucket, multipart sources, fresh checksums) | ✅ |
| UploadPartCopy (`x-amz-copy-source-range`, source versions and conditions, the upload checksum computed while copying) | ✅ (6b) |
| Versioning: Get/PutBucketVersioning (MFA delete refused), version ids and delete markers on every read/write/delete, ListObjectVersions, DeleteObjects with versions, copy from a version | ✅ (6a) |
| Bucket lifecycle: Get/Put/DeleteBucketLifecycle (expiration, noncurrent expiration, expired delete markers, abort incomplete uploads; prefix and size filters) | ✅ (6c) |
| Bucket policy: Get/Put/DeleteBucketPolicy, GetBucketPolicyStatus; evaluation on every request (Deny > Allow > key scope), anonymous access when a policy allows it, conditions (source IP, TLS, prefix, max-keys, …) | ✅ (6d) |
| Presigned URLs | ✅ verification (any operation) |

## Backlog (not scheduled)

Features TimeStore does not have and no phase has taken yet. Until one lands, S3 requests that use it are refused
(`NotImplemented`, or the error Amazon S3 gives), never silently ignored. Each item gets research → decisions →
roadmap step before code, like every other step.

| Feature | Today | Notes |
|---|---|---|
| Erasure coding | Not planned before phase 8 | Decided: replicas first, EC for cold (warm) data only, later (`docs/architecture.md`) |
| Replication between sites | Refused (`?replication`: `NotImplemented`) | Phase 8 replicates within one cluster only; cross-site (bucket replication rules) would come after it |
| Full IAM: users, groups, user policies, STS / temporary credentials | Access keys with an admin role and an S3 scope, plus bucket policies (6d) | Single tenant today (`docs/research/s3-bucket-policy.md` §2); multi-tenant accounts would change bucket ownership too. ceph markers `iam_*`, `user_policy`, `group_policy`, `role_policy`, `test_of_sts` stay excluded |
| Server-side encryption (SSE-S3, SSE-C, SSE-KMS) | Refused (`NotImplemented`) | `docs/research/s3-objects.md`; ceph markers `encryption`, `bucket_encryption`, `sse_s3` |
| Object lock (retention, legal hold) | Refused (`NotImplemented`) | Needs versioning (done in 6a); `docs/research/s3-versioning.md`, `s3-objects.md`; ceph marker `object_lock` |
| Object and bucket tagging | Refused (`NotImplemented`) | Also unlocks lifecycle tag filters (refused today, `docs/research/s3-lifecycle.md`); ceph marker `tagging` |
| CORS configuration | Refused (`?cors`: `NotImplemented`) | Needed for browsers calling the S3 port directly; `docs/research/s3-protocol.md` |
| Static website hosting | Refused (`?website`: `NotImplemented`) | ceph markers `s3website*` |
| Event notifications (webhook, queue) | Refused (`?notification`: `NotImplemented`) | No research yet; ceph marker `sns` |
| Favourites, shared-files list, recycle bin, rename (in the 7i design) | Not supported: hidden in the UI | Need object metadata TimeStore does not keep (stars, soft delete) or a server-side rename (copy + delete) |
| Cross-bucket search, storage by file type, traffic per day | Not supported: the dashboard shows requests by type and storage per bucket instead | Need new metrics or an index; the admin API has none today |
| Faster GET and STAT (beat RustFS) | GET 64 KiB × 16: 30,213 obj/s vs RustFS 38,539; STAT × 16: 47,116 vs 49,843 (`docs/benchmarks/2026-11-17-compare.md`, macOS). On the 4-vCPU Linux VPS GET is a tie (4,124 vs 4,217) and STAT is ahead (6,110 vs 5,165) | Profile the read path first (metadata lookup per request, blob open, response headers, copy loop); keep the AOT and memory budget; re-run `deploy/bench/compare.sh` on Linux to confirm |
| Faster single-client small PUTs | PUT 1 KiB × 1: 894 obj/s vs Garage 1,218 (macOS); 199 vs 254 on the 4-vCPU Linux VPS | Each lone write waits for its own fsync (group commit only helps concurrent writers); look for a cheaper commit without weakening durability |
| Bulk folder operations as background jobs | Deleting a folder runs in one admin request (page by page) | With millions of objects: a job with progress and cancel, like the lifecycle runner; also for "copy/move folder" |
| Browser-form POST upload (PostObject) | Refused: anonymous → `AccessDenied`, signed → `NotImplemented` | `docs/research/s3-objects.md`, `s3-bucket-policy.md` §4; ceph `test_post_object_*` |
| Storage classes and transitions | Only `STANDARD`; lifecycle transitions refused (`InvalidStorageClass`) | `docs/research/s3-lifecycle.md`; ceph markers `storage_class`, `lifecycle_transition` |
| ACLs beyond "bucket owner enforced" | As Amazon S3 with ACLs disabled: only `bucket-owner-full-control` | README; bucket policies (6d) cover access control |
