# Keeping deleted blobs: retention and metadata-only backups (B5a built)

Sources (checked 2026-11-20, links in "Sources" at the end): Ceph RGW garbage collection (`rgw_gc_obj_min_wait`),
Garage block reference counts (`BLOCK_GC_DELAY`), MinIO's `.trash` purger, SeaweedFS vacuum, JuiceFS trash and
metadata backups; this repository's `docs/architecture.md` (write path, deferred deletion), `docs/research/backup.md`
(archives, blob hold) and `docs/research/replication.md`.

Status: **B5a built** (2026-11-20, see "B5a as built"); B5b and B5c proposed. Stages are in the roadmap ("Data protection", B5).

## The problem

A backup today is an **archive**: the metadata at instant T plus every blob it references, each blob read and checked.
That is right for small and medium servers, and wrong for large ones: every run reads all the data again (hours for
terabytes), although blobs never change.

Blobs are immutable and named by a UUIDv7, so the data could be copied **incrementally** (only blobs that are new
since the last copy) and the metadata backed up on its own, as a small `.db` file, often. What stops this today is
deletion: a blob released by an overwrite or a delete is removed from disk **60 seconds** later
(`Blobs:DeleteDelaySeconds`), so a metadata backup from yesterday points at blobs that no longer exist.

The fix is old and simple: **keep released blobs for a retention period**. A metadata backup of instant T is then
restorable for as long as every blob released after T is still kept, on disk or in the blob copy:

```
day 0      metadata backup B0 (references blob X)
day 0.5    X is overwritten: released at day 0.5
day 30     B0 is still kept (backups kept 30 days) → restoring B0 needs X
day 35.5   X is deleted (retention 35 days = backups kept 30 days + a margin)
```

Retention counts from the moment a blob is **released**, not from its upload.

## How others do it

| System | Deleted data | Retention |
|---|---|---|
| **Ceph RGW** | Tail objects of a deleted or overwritten object go to a GC queue (`radosgw-admin gc list`); the head goes at once | `rgw_gc_obj_min_wait`, default 2 hours: long enough for reads in progress. A 2020 bug (Octopus 15.2.0–15.2.6) let slow reads queue tails they were still reading, and data was lost |
| **Garage** | Blocks are reference counted; at zero the block becomes deletable after a delay | `BLOCK_GC_DELAY` = 10 minutes (table entries: one day) |
| **MinIO** | Deleted data is moved to a per-drive `.trash` and purged in the background | Minutes (`delete_cleanup_interval`); no retention for restores |
| **SeaweedFS** | Deleted needles stay in their volume file until a vacuum copies the live ones out | Until garbage passes a threshold (30% by default) |
| **JuiceFS** | Deleted files and stale slices (overwritten data) go to a **trash**; metadata is backed up hourly by the client (`--backup-meta`) | `--trash-days`: "if files are erroneously edited or overwritten, original state can be recovered through metadata backups", while the trash keeps the slices |

Ceph, Garage and MinIO delay deletion only to protect reads in flight (what TimeStore's 60 seconds do). **JuiceFS is
the model for this note**: a metadata engine plus immutable data objects, hourly metadata backups, and a trash
period that keeps every slice an older metadata backup may need. The rule a user shared from a design discussion
("keep backups 30 days, delete released chunks after 35") is the same idea.

## Design

### 1. Released blobs go to a table, in the commit's transaction

Today a commit returns the data it released, the service calls `IBlobDeletions.DeferAsync`, and an in-memory FIFO
deletes it after 60 seconds. A crash in between forgets the queue (the orphan sweep removes those blobs an hour
later). For a retention of days the queue must be durable, and it must be written **atomically with the commit**:
otherwise a crash leaves a released blob looking like an orphan, the sweep deletes it after an hour, and every
metadata backup that references it is broken.

```sql
-- Migration 13
CREATE TABLE pending_blob_deletions (
    blob_id     BLOB    NOT NULL PRIMARY KEY,
    node_id     TEXT    NOT NULL,                 -- the node whose disk holds it (several nodes later)
    size        INTEGER NOT NULL,                 -- retained bytes (metrics)
    released_at INTEGER NOT NULL                  -- unix ms UTC: when the last reference went
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_pending_blob_deletions_released ON pending_blob_deletions (node_id, released_at);
```

- Written by the **store**, inside the write that releases the blob: `CommitObjectAsync` (the replaced data),
  `CompleteUploadAsync` (replaced data and unlisted parts), `CommitPartAsync` (a replaced part), `AbortUploadAsync`,
  `DeleteBucketIfEmptyAsync` (parts of open uploads), `DeleteObjectVersionsAsync`. These are all the places that
  release a committed blob (the nine `DeferAsync` calls in services all pass what these return). Services stop
  deferring committed blobs; they keep deleting at once the blobs of writes that never committed.
- The row stores `released_at`, **not** a due time: the wait is computed by the collector from the configuration, so
  raising the retention also protects blobs already released.
- One row per released blob, in the same transaction and the same fsync as the commit: an overwrite adds one row, a
  multipart overwrite one per part. No extra fsync, nothing on reads.
- `FindUnreferencedBlobsAsync` (orphan sweep) treats rows of this table as referenced: the one place that knows every
  table holding blob references gains one more, and `IBlobDeletions.IsPending` (in memory) goes away.

### 2. The collector

`BlobCollector` (a worker, replacing the drain loop of `DeferredBlobDeletions`): every `Blobs:CollectSeconds`
(default 10), take rows of this node with `released_at <= now - wait`, oldest first, in batches of 500; delete each
file, then the rows. A crash between the two leaves rows for missing files; deleting a missing blob is not an error.

- `wait = max(Blobs:DeleteDelaySeconds, Blobs:RetainDeletedDays)`. Default retention **0**: blobs go after 60 seconds,
  exactly as today.
- While a backup holds blobs (`IBlobHold`), the collector does not run (the hold keeps its meaning; it is now a flag
  the collector checks instead of a gate on an in-memory queue).
- Shutdown no longer deletes the queue: rows wait for the next start.
- Several nodes later: each node collects only its own `node_id` (blobs are on its disk), under the lease every
  background worker will need; the table lives in the shared metadata database, which an in-memory queue cannot.

### 3. Metadata-only backups

A backup run with `content: metadata`: the archive holds `manifest.json` and `metadata.db` (the same `VACUUM INTO`
snapshot), no blobs, so it is small and takes seconds. The manifest records the retention in force and the newest
blob id at T.

- Refused with `BACKUP_RETENTION_REQUIRED` while `Blobs:RetainDeletedDays` is 0: such a backup would be useless after
  a minute.
- The snapshot keeps `pending_blob_deletions` (the collector goes on after a restore).
- The full archive (`content: full`, today's only kind) stays the default and is unchanged.

### 4. Restoring a metadata-only backup

`restore --metadata <archive>` into a data directory whose blobs are present (the same server, or a copy of its
`blobs/`): the server is stopped, `metadata.db` is replaced, then **every blob the restored metadata references is
checked** (exists, size). Missing blobs are listed with their keys, and the restore fails unless `--allow-missing`
(those objects then answer an error until deleted).

No other reconcile is needed:

- blobs written after T are not referenced by the restored metadata: they are orphans, and the sweep removes them
  (after its grace period);
- blobs released after T are referenced again by the restored metadata, and the restored `pending_blob_deletions`
  (from T) does not list them: they are live again;
- blobs released before T are in the restored table: the collector goes on with them.

As with any restore, every write after T is lost (deletes included: what was deleted after T is back).

### 5. Incremental data copies

Because blob files never change and appear only by an atomic rename into `blobs/`, copying the directory with a tool
that skips existing files (`rclone copy`, never `sync`) is already an incremental, consistent blob copy. The order
that makes a metadata backup restorable from such a copy: **metadata backup first (T), blob copy after**, and the copy
keeps every blob for at least the retention (a later prune deletes only blobs older than the retention that no kept
metadata backup references). A built-in copy to an S3 endpoint is stage B4/B5c.

### 6. Configuration

| Key | Default | Meaning |
|---|---|---|
| `Blobs:RetainDeletedDays` | `0` | Days a released blob is kept before it is deleted (0–3650). Set it to the retention of metadata backups plus a margin (backups kept 30 days → 35) |
| `Blobs:DeleteDelaySeconds` | `60` | Unchanged: the shortest wait (reads in flight) |
| `Blobs:CollectSeconds` | `10` | How often the collector runs (1–3600) |

Disk space: everything overwritten or deleted stays for the retention. The metrics gain "retained bytes" (the sum of
`size` in the table) so the cost is visible; `Backup:MinFreeBytes` and the admin UI's disk figures already count it.

## Stages

| Stage | Content | Tests |
|---|---|---|
| **B5a** ✅ | `pending_blob_deletions` written by the six store methods; collector; orphan sweep reads the table; `Blobs:RetainDeletedDays`, `Blobs:CollectSeconds`; retained bytes metric. Behaviour unchanged at retention 0 | Each write path adds its rows in the commit's transaction; a crash after a commit keeps the rows (the sweep never deletes them); retention honoured, raising it protects blobs already released; a hold stops the collector |
| **B5b** | Metadata-only backups (`content: metadata`) and `restore --metadata` with the blob check | Restore yesterday's metadata after overwrites and deletes: every object reads; with retention too short, the missing blobs are listed |
| **B5c** | Built-in incremental blob copy to an S3 endpoint (or documented `rclone copy` only) | A second copy sends only new blobs |

## B5a as built (2026-11-20)

As designed, with these details:

- `IBlobDeletions` now has `ReleasedAsync()` (a service reports that its commit released blobs; with no wait the
  collector runs at once, otherwise nothing happens) and `CollectAsync()`. `DeferAsync` and `IsPending` are gone: the
  store records the blobs, the sweep asks the store. `DeferredBlobDeletions` became `BlobCollector` (also the
  `IBlobHold`).
- The collector deletes a batch's files first, then forgets their rows; it runs every `Blobs:CollectSeconds`, when a
  hold ends, and on a report when the wait is zero. A round that fails leaves the rows for the next one.
- Metrics: `storage.retainedBlobs` and `storage.retainedBytes`.
- Tests: `MetadataStoreContract` (every releasing write records its blobs, per-node listing, released blobs are not
  orphans), `BlobCollectorTests` (grace, retention, raising it after the release, a restart, holds, zero wait, nodes),
  `BlobRetentionTests` end to end (released blobs survive the grace, a kill and a restart; the metric), and
  `BackupCrashTests` (after a crash during a backup, the held blob is collected without the orphan sweep).

## Decisions to review

| # | Proposal | Why |
|---|---|---|
| 1 | Durable table written in the commit's transaction, by the store | A crash never turns a released blob into an orphan that the sweep deletes |
| 2 | `released_at`, wait computed from configuration | Raising the retention protects blobs already released |
| 3 | Default retention 0 | No extra disk for anyone who does not use metadata-only backups |
| 4 | Metadata-only backups refused at retention 0 | They would be unusable after a minute |
| 5 | Full archives stay the default | Self-contained; right for small and medium servers |
| 6 | `node_id` in the table now | Several nodes collect their own disks without a migration |

## Sources

- Ceph: [RGW configuration reference](https://docs.ceph.com/en/tentacle/_sources/radosgw/config-ref.rst.txt) (`rgw_gc_obj_min_wait`), [Octopus GC data-loss bug](https://bugzilla.redhat.com/show_bug.cgi?id=1892644)
- Garage: [`block.rs` (`BLOCK_GC_DELAY`)](https://docs.rs/garage_model/0.6.0/src/garage_model/block.rs.html), [internals](https://garagehq.deuxfleurs.fr/design/internals.html)
- MinIO: [troubleshoot system path growth (`.trash`)](https://docs.min.io/aistor/operations/troubleshoot-system-path-growth/)
- SeaweedFS: [FAQ (vacuum threshold)](https://github.com/seaweedfs/seaweedfs/wiki/FAQ)
- JuiceFS: [trash](https://juicefs.com/docs/community/security/trash), [garbage collection](https://juicefs.com/en/blog/engineering/juicefs-garbage-collection), [metadata backup and recovery tips](https://juicefs.com/en/blog/usage-tips/juicefs-user-tips-distributed-file-storage-system)
