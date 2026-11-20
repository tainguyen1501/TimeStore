# Bucket replication (R1 built)

Sources (re-checked 2026-11-19, links in "Sources" at the end): Amazon S3 replication (status, Batch Replication,
metrics), MinIO bucket replication (queue, resync, resync-backlog), Ceph RGW multisite (logs, full and incremental
sync), SeaweedFS `filer.sync` / `filer.backup` (change log, checkpoints, signatures); this repository's
`docs/architecture.md` (write path, immutable blobs, deferred deletion) and `docs/research/backup.md` (the blob
hold).

Status: **R1 built** (2026-11-19; what changed from the proposal: "R1 as built" below); R2–R4 proposed. Overview of all mechanisms: [../data-protection.md](../data-protection.md). Stages R1–R4 are in the
roadmap ("Data protection").

## Goal

Every change of a bucket reaches another S3 endpoint (MinIO, AWS S3, Cloudflare R2, Backblaze B2, another TimeStore)
within seconds, without slowing writes and without losing a change on a crash, so that:

- a server or site lost loses seconds of data, not a day;
- the destination is a working S3 endpoint: clients switch to it, no restore;
- the load is proportional to what changes, not to what is stored (unlike a full backup or a listing sync).

Not goals: synchronous writes to two places (that is multi-node, phase 8), replicating bucket configuration
(policies, lifecycle, versioning: set on the destination), two-way sync (R4 at the earliest).

## How others do it

Two families: a **status on every object plus a scanner** (Amazon S3, MinIO), or a **change log plus a sync position**
(Ceph, SeaweedFS).

| Server | Change capture | Retries and failures | Existing objects |
|---|---|---|---|
| **Amazon S3** | Per-bucket rules; status per object: `PENDING`, `COMPLETED`, `FAILED`, `REPLICA` | A **temporary** failure (destination or Region unavailable) stays `PENDING` and resumes on its own; a `FAILED` object cannot be retried (upload it again, or Batch Replication). Lifecycle does not expire or transition objects that are `PENDING` or `FAILED` | Batch Replication (also for failed ones); delete markers optional, with an ordering pitfall when expired markers meet lifecycle |
| **MinIO** | Status in each object's metadata; an in-memory queue | Three attempts, then dequeued; a background **scanner** re-queues `PENDING` and `FAILED` objects; a GET, HEAD or LIST of one re-queues it too | `resync` (every object again, after the target lost data), `resync-backlog` (only stuck ones); existing-object replication goes through the same scanner and queue |
| **Ceph RGW multisite** | Metadata log, data log, bucket index log; each zone keeps its sync position per peer | Failed entries go to a retry list | init → **full sync** → **incremental**; the log position is **recorded before** the full sync, so incremental picks up what changed during it. A known bug: a delete during a full sync was missed and the bucket stayed out of sync |
| **SeaweedFS `filer.sync`** | Follows the filer's metadata change log | Resumes from a checkpoint saved every few seconds (a final save on shutdown was added in 2025); each change carries its filer's **signature**, so active–active never ping-pongs | Bootstraps from the beginning of the log |

Common points: asynchronous; a durable record of what changed; per-object or per-entry status; deletes optional;
existing objects through a separate pass that must not miss changes made meanwhile.

**TimeStore's outbox is a change log in the same SQLite transaction as the commit**: nothing can be lost between the
commit and the queue, so no scanner is needed to find what an in-memory queue dropped (MinIO's design needs one).
What replication sends is objects through the S3 API (content, content headers, user metadata, deletes): the
destination keeps its own metadata. Access keys, policies, lifecycle and versioning settings are configuration of the
destination; a whole-server copy with keys is a backup archive.

## Design

### Rules

Per bucket (admin API and UI first; the S3 `PutBucketReplication` format in R3):

```json
{
  "destination": { "endpoint": "https://<account>.r2.cloudflarestorage.com", "bucket": "photos-replica",
                   "region": "auto", "accessKeyId": "...", "secretKey": "..." },
  "prefix": "",
  "replicateDeletes": false,
  "existingObjects": true
}
```

The destination secret key is encrypted with the master key, like access-key secrets (`ISecretProtector`), never
returned or logged. A **test** action writes, reads and deletes a marker object at the destination before the rule is
saved.

### Change capture: a transactional outbox

Every commit that changes a replicated bucket inserts a row in `replication_queue` **in the same transaction**. The
five write methods of the metadata store are the only places that change objects, whoever calls them (S3 API, admin
UI, lifecycle): `CommitObjectAsync` (put, copy), `CompleteUploadAsync`, `DeleteObjectAsync`, `DeleteObjectsAsync`,
`DeleteObjectVersionsAsync`. Writing the row there covers every path. A crash after the commit cannot lose the change;
nothing is kept only in memory. A bucket without a rule writes no row (one lookup in a cached rule set per commit).

```sql
CREATE TABLE replication_rules (
    id                BLOB    NOT NULL PRIMARY KEY,       -- UUIDv7, shown by the admin API
    bucket_id         BLOB    NOT NULL UNIQUE,            -- one rule (one destination) per bucket in R1
    endpoint          TEXT    NOT NULL,                   -- https://<account>.r2.cloudflarestorage.com
    target_bucket     TEXT    NOT NULL,
    region            TEXT    NOT NULL,
    access_key_id     TEXT    NOT NULL,
    secret_ciphertext BLOB    NOT NULL,                   -- ISecretProtector, never returned or logged
    prefix            TEXT    NOT NULL,
    replicate_deletes INTEGER NOT NULL,
    created_at        INTEGER NOT NULL
) STRICT;

CREATE TABLE replication_queue (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,       -- order of the changes; never reused (logs, retry)
    rule_id      BLOB    NOT NULL,
    object_key   TEXT    NOT NULL,
    version_id   TEXT,
    operation    TEXT    NOT NULL,                        -- put | delete
    created_at   INTEGER NOT NULL,
    attempts     INTEGER NOT NULL DEFAULT 0,
    next_attempt INTEGER NOT NULL,
    status       TEXT    NOT NULL,                        -- pending | failed
    last_error   TEXT,
    CHECK (operation IN ('put', 'delete')),
    CHECK (status IN ('pending', 'failed'))
) STRICT;
CREATE INDEX ix_replication_queue_due ON replication_queue (status, next_attempt, id);
CREATE INDEX ix_replication_queue_key ON replication_queue (rule_id, object_key);
```

A row is one **change**, not one file: a key overwritten five times has five rows until the worker sends its current
state once and deletes them. The table holds what is waiting (normally seconds of changes), not a list of objects;
rows are a few dozen bytes (no content). A 64-bit id never runs out.

### The worker

- Takes due rows oldest first, a bounded number in parallel (`Replication:Concurrency`), with a bandwidth limit.
- **Coalesces** per key on unversioned buckets: when a key changed several times while waiting, only its current
  state is sent (put the latest, or delete), so a key overwritten in a loop never floods the queue.
- Put: reads the blob (or the parts) and sends `PutObject`, or a multipart upload above a size threshold, with the
  object's content type, headers and user metadata; then compares the destination's checksum with the stored MD5
  (or the part checksums). Delete: `DeleteObject`, only with `replicateDeletes`.
- **Temporary failures never become `failed`** (as Amazon S3): a network error, a timeout, a 5xx or a throttling
  answer retries with exponential backoff, capped (for example at 15 minutes), for as long as it takes. Only a
  **permanent** answer marks the row `failed` and shows it in the UI: credentials refused (403), the target bucket
  missing (404 `NoSuchBucket`), the object rejected (400). A retry action (per row or per rule) sets it back to
  `pending`. The destination being down never fails a client request.
- No blob hold (changed in R1): the worker reads the key's **current** state when it sends, so it never needs an old
  blob. A blob released meanwhile stays readable for the grace period (and an open file survives its deletion on
  Linux); a part deleted before it is opened makes the send fail as "source unavailable", and the key is tried again
  with its newer state.

### Existing objects

`existingObjects: true` queues every current object (R1: in the rule's own transaction, see "R1 as built") (and every version
on versioned buckets, R3), throttled like the worker. **The rule starts capturing changes before the walk starts**
(Ceph records its log position first): a change made during the walk is in the queue as well, and coalescing makes the
newest state win, so a key deleted or overwritten during the backfill is never left stale. A test covers exactly
that case (the bug Ceph had).

**Resync** (MinIO's `resync`, R2): queue every object of the bucket again, for a destination that lost data or was
replaced.

### Lifecycle and deletions

No blob hold (see "The worker"). Lifecycle expiration of an object still pending is allowed (it is a change like any other: the delete goes to the queue, and
coalescing sends the newest state).

### Visibility

- `x-amz-replication-status` on GET/HEAD: `PENDING`, `COMPLETED`, `FAILED` (as Amazon S3).
- Admin UI: per rule, lag (age of the oldest pending row), pending count, failures with the object and error; metrics.

### S3 client inside TimeStore

The worker needs to call another S3 endpoint from a Native AOT binary. Options: a minimal client (PutObject,
multipart, DeleteObject, HeadObject, signed with the SigV4 code already in `TimeStore.Cryptography`), or AWSSDK.S3 if
it publishes under Native AOT without warnings. Decide in R1 with a spike; behind an `IReplicationTarget` port either
way.

### Failover

The destination is a normal bucket: switch clients to its endpoint. With another TimeStore as the destination this is
an active–passive pair; making the old primary a replica of the new one is R4.

## Stages

| Stage | Content | Tests |
|---|---|---|
| **R1** ✅ | Rules (admin API, a test action), outbox in the five write methods, worker (put, delete, coalescing, temporary vs permanent failures, Content-MD5 and ETag check), one destination per bucket, the S3 client, existing objects queued with the rule | Every change reaches a second TimeStore; a crash between commit and send loses nothing; destination down then up catches up; a key overwritten 100 times sends a few puts; a 5xx stays pending, a 403 becomes failed and retries on demand; every write path (S3, admin UI, lifecycle) queues its change |
| **R2a** ✅ | Admin UI page (rules, lag, failures, retry, test, remove), resync, metrics | Resync after the destination lost an object; the rule in the metrics; UI checked by hand with two servers (refused test, saved, up to date, paused) |
| **R2b** | Status header (`x-amz-replication-status`), bandwidth limit | Status values; GET/HEAD latency unchanged on buckets without a rule |
| **R3** | Versioned buckets: every version and delete marker in order; S3 `PutBucketReplication` / `GetBucketReplication` / `DeleteBucketReplication` | Versions identical on both sides; AWS SDK round trip of the configuration |
| **R4** | Two-way or site replication between TimeStore servers, switching direction after a failover | Loop prevention: a change that came from replication is marked (`REPLICA` status, like SeaweedFS's filer signature) and never sent back; conflict rule (last writer wins) |
| Later | Several nodes: the worker under a lease, sending from a replica node | — |

## Decisions to review

| # | Proposal | Why |
|---|---|---|
| 1 | Asynchronous only | Writes stay as fast as today; a slow destination never fails clients |
| 2 | Outbox row in the commit's transaction | No lost change on a crash, no in-memory state |
| 3 | Coalesce per key on unversioned buckets | Continuous overwrites do not flood the queue or the link |
| 4 | Deletes off by default | A mistake on the source is not copied; turn on with a versioned destination |
| 5 | Destination credentials encrypted with the master key | Same model as access keys |
| 6 | Admin API first, S3 replication API in R3 | The S3 format requires role ARNs and versioning; the admin form is simpler to start with |

## R1 as built

Decided while building R1 (2026-11-19); everything else is as designed above.

| Topic | As built | Why |
|---|---|---|
| S3 client | Own small client in the Server (`Replication/S3ReplicationClient`): `HttpClient`, SigV4 header signing (`Protocol/Auth/SigV4RequestSigner`, verified by `SigV4Verifier` in tests), `XmlReader`; no AWSSDK.S3 | No new production package; Native AOT publish stays warning-free; four operations only |
| Sending an object | The key's **current** state, read when sent. Single blob: one PUT with the blob's `Content-MD5`. Multipart: a multipart upload with the **source's own parts** (each with its MD5), aborted on failure. The destination's ETag is compared when it has the same form | No re-chunking setting, no buffering; the destination refuses a changed body; the replica's ETag equals the source's |
| Coalescing | Per key: the worker remembers the newest change id it read, sends, then deletes the key's rows up to that id; changes committed during the send stay queued | A key overwritten in a loop is sent a few times, never lost |
| Parallelism | Up to `Concurrency` keys at once, a key never twice at once; a slot frees as soon as its key is done | A large object never holds back the small ones |
| Failure classes | Temporary (network, timeout, 5xx, 429, `SlowDown`, `RequestTimeout`, `NoSuchUpload`): the **rule** pauses with a backoff (1, 2, 4 … s up to `MaxBackoffSeconds`). Rule-wide refusal (401/403, `NoSuchBucket`, redirects): every pending change of the rule fails. Object refusal (other 4xx): that key fails. Source unreadable: that key retries later | One probe per backoff while a destination is down, not one request per key |
| Wake-up | The store signals after a commit that queued a row (a bounded channel of one); the worker waits `BatchDelayMilliseconds` after a wake-up; `PollSeconds` is a safety net | Seconds of lag without polling the database |
| No rule, no cost | The store keeps the number of rules; with none, a commit runs no extra SQL | The hot path is unchanged for servers that do not replicate |
| Existing objects | Queued by an `INSERT … SELECT` in the same transaction as the rule (R1, moved from R2) | No change can fall between the walk and the outbox (Ceph bug #58939); a huge bucket holds writes for the seconds that insert takes |
| Saving a rule | Tests the destination first; saving again keeps the id and the queue and retries failed changes; the secret is encrypted with the bucket id as associated data | One rule per bucket, editable without losing what waits |
| Versioned buckets | Allowed; only the current state is sent (a deleted latest version sends the older one) | Full version replication is R3 |
| Backups | Snapshots leave `replication_rules` and `replication_queue` empty | A restored server must not push its older state over a newer replica |
| Settings | `TimeStore:Replication:Enabled`, `Concurrency`, `PollSeconds`, `BatchDelayMilliseconds`, `MaxBackoffSeconds`, `RequestTimeoutSeconds`, all with defaults | Changed without a release |

Not in R1: a loop guard (a rule pointing at its own bucket, or two servers at each other, would copy forever: R4),
a bandwidth limit, resync and the UI page (R2).

Tests: `ReplicationStoreContract` (each write path queues, prefix, deletes, coalescing bound, backfill, pause, fail,
retry, cascade), `ReplicationRunnerTests` (a hot key sent once, changes during a send, a slow object, failure
classes), `SigV4RequestSignerTests`, and `ReplicationTests` end to end with a second TimeStore (headers, ETag of a
multipart copy, existing objects, prefix, deletes off, destination down plus a source crash, refused key then retry),
also on the AOT binary.

## Sources

- Amazon S3: [replication status](https://docs.aws.amazon.com/AmazonS3/latest/userguide/replication-status.html), [Batch Replication](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-batch-replication-batch.html), [replication metrics](https://docs.aws.amazon.com/AmazonS3/latest/userguide/repl-metrics.html)
- MinIO: [bucket replication](https://docs.min.io/community/minio-object-store/administration/bucket-replication.html), [mc replicate resync](https://docs.min.io/community/minio-object-store/reference/minio-mc/mc-replicate-resync.html), [resync-backlog](https://docs.min.io/aistor/reference/cli/mc-replicate/mc-replicate-resync-backlog/), [resynchronize after data loss](https://docs.min.io/community/minio-object-store/administration/bucket-replication/server-side-replication-resynchronize-remote.html)
- Ceph: [new multisite sync](https://tracker.ceph.com/projects/ceph/wiki/Rgw_new_multisite_sync), [sync agent architecture](https://tracker.ceph.com/projects/ceph/wiki/Rgw_sync_agent_architecture), [bug #58939](https://tracker.ceph.com/issues/58939)
- SeaweedFS: [filer active-active sync](https://github.com/seaweedfs/seaweedfs/wiki/Filer-Active-Active-cross-cluster-continuous-synchronization), [async backup](https://github.com/seaweedfs/seaweedfs/wiki/Async-Backup)

