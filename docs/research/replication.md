# Bucket replication (design, not built)

Sources: Amazon S3 replication (`PutBucketReplication`, `x-amz-replication-status`, S3 Batch Replication), MinIO
bucket and site replication, SeaweedFS `filer.backup` / `filer.remote.sync`, all from their public documentation as
known in 2026-11 (re-check before building); this repository's `docs/architecture.md` (write path, immutable blobs,
deferred deletion) and `docs/research/backup.md` (the blob hold).

Status: proposed. Overview of all mechanisms: [../data-protection.md](../data-protection.md). Stages R1–R4 are in the
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

| Server | Model | Notes |
|---|---|---|
| Amazon S3 | Per-bucket rules (prefix / tag filters) to a bucket in the same or another region, asynchronous; status per object (`PENDING`, `COMPLETED`, `FAILED`, `REPLICA`); Batch Replication for existing objects; delete markers optional | Destination needs versioning |
| MinIO | Bucket replication (per bucket, to any S3 target, sync or async), site replication (whole deployments, two-way, includes IAM and configuration) | Versioning required |
| SeaweedFS | `filer.backup` / `filer.remote.sync`: a process that follows the filer's change log and writes to another store | Outside the server process |

Common points: asynchronous, a durable change log or queue, per-object status, deletes optional, existing objects
copied by a separate job.

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

Every commit that changes a replicated bucket (put, complete multipart, copy, delete, delete marker) inserts a row in
`replication_queue` **in the same transaction**: bucket, key, version id, operation, created at, attempts, next
attempt, status. A crash after the commit cannot lose the change; nothing is kept only in memory. A bucket without a
rule writes no row (one lookup in a cached rule set per commit).

### The worker

- Takes due rows oldest first, a bounded number in parallel (`Replication:Concurrency`), with a bandwidth limit.
- **Coalesces** per key on unversioned buckets: when a key changed several times while waiting, only its current
  state is sent (put the latest, or delete), so a key overwritten in a loop never floods the queue.
- Put: reads the blob (or the parts) and sends `PutObject`, or a multipart upload above a size threshold, with the
  object's content type, headers and user metadata; then compares the destination's checksum with the stored MD5
  (or the part checksums). Delete: `DeleteObject`, only with `replicateDeletes`.
- Failures retry with exponential backoff; after N attempts the row is `failed` and shown in the UI; a retry action
  resets it. The destination being down never fails a client request.
- Blobs referenced by a pending row are not deleted until it is done: the same hold as backups, per blob instead of
  global (deferred deletion skips them).

### Existing objects

`existingObjects: true` starts a backfill job that walks the bucket and queues every current object (and every version
on versioned buckets, R3), throttled like the worker.

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
| **R1** | Rules (admin API), outbox in the commit, worker (put, delete, coalescing, retries, checksum check), one destination per bucket, the S3 client, blob hold per pending row | Every change reaches a second TimeStore; a crash between commit and send loses nothing; destination down then up catches up; a key overwritten 100 times sends a few puts |
| **R2** | Backfill of existing objects, status header, admin UI page (rules, lag, failures, retry), metrics | Backfill of 10 000 objects; status values; UI checks |
| **R3** | Versioned buckets: every version and delete marker in order; S3 `PutBucketReplication` / `GetBucketReplication` / `DeleteBucketReplication` | Versions identical on both sides; AWS SDK round trip of the configuration |
| **R4** | Two-way or site replication between TimeStore servers, switching direction after a failover | Loop prevention (`REPLICA` status), conflict rule (last writer wins) |
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
