# Data protection

How to keep TimeStore's data safe: six mechanisms, what each protects against, and which to use for your size.
Built ones are documented in their own pages; planned ones are marked **Soon** and linked to the roadmap.

| Mechanism | What it is | Status | Page |
|---|---|---|---|
| **Full backups** | A point-in-time archive of the whole server (metadata and every blob at one instant, every blob verified), taken while it runs; restored with a command into an empty data directory | 🧪 Beta | [backup.md](backup.md) |
| **Metadata-only backups** | A point-in-time archive of the metadata alone, while the blobs stay on disk for `Blobs:RetainDeletedDays`; restored in place, it puts the whole server back at that instant | 🧪 Beta | [backup.md](backup.md#metadata-only-backups-undo-to-a-point-in-time) |
| **Versioning** | Older versions of every object are kept, so a delete or an overwrite can be undone | ✅ Built | [user guide](user-guide/README.md) |
| **Volume snapshots** | Your storage snapshots the data volume (a CSI `VolumeSnapshot`, ZFS, LVM, a cloud disk); no TimeStore feature needed | ✅ Works today | [backup.md](backup.md#2-snapshot-the-volume) |
| **Mirroring** | An outside tool (`rclone sync`) copies buckets to another storage on a schedule | ✅ Works today, no TimeStore feature needed | [mirroring.md](mirroring.md) |
| **Replication** | TimeStore itself sends every change of a bucket to another S3 endpoint, right after the commit | ✅ Built (admin UI and API) | [guide](user-guide/replication.md), [design](research/replication.md) |

TimeStore runs as **one server** (one pod on Kubernetes, whatever the cluster's size); several TimeStore nodes sharing
data come with phase 8 of the roadmap. "Small" and "large" below are about the data and the write rate, not the number
of Kubernetes nodes.

**Several nodes do not replace backups.** Replicas across nodes protect against a lost disk or machine, not against a
mistake: a delete, a bad write or ransomware reaches every replica within seconds. An undo (versioning, a metadata-only
backup) is needed at any size.

## What each one protects against

| Event | Full backups | Metadata-only backups | Versioning | Volume snapshots | Mirroring | Replication |
|---|---|---|---|---|---|---|
| Disk, VPS or cluster lost | ✅ (archive kept elsewhere) | ❌ (the blobs are on that disk) | ❌ (same disk) | ⚠️ only if the snapshot is copied off the storage | ✅ | ✅ |
| Object deleted or overwritten by mistake | ✅ back to the last backup | ✅ back to the last backup, within the retention | ✅ any older version | ✅ back to the last snapshot | ⚠️ only with `rclone copy` or a versioned destination | ⚠️ only with deletes off or a versioned destination |
| Bad writes or ransomware on many objects | ✅ an older archive is untouched | ✅ within the retention | ✅ if versions are kept long enough | ✅ if the storage keeps older snapshots | ❌ the next sync copies the damage | ❌ copied within seconds, unless the destination keeps versions |
| Whole server back to a given day | ✅ | ✅ within the retention | ❌ (per object only) | ✅ | ❌ | ❌ |
| Move to another machine or cluster | ✅ one file | ❌ | ❌ | ⚠️ same storage system only | ✅ bucket by bucket | ✅ |

## Size, downtime, loss

| | Full backups | Metadata-only backups | Volume snapshots | Mirroring | Replication |
|---|---|---|---|---|---|
| Grows with | All the data (bytes) | The number of objects (the database) | Nothing (copy-on-write, near instant) | The number of objects (each run lists both sides) | The changes |
| Server stopped to take it | No | No | No | No | No |
| Server stopped to restore | Yes (into an empty directory) | Yes (in place, minutes at most) | Yes (roll back or attach the snapshot) | No: clients use the copy | No: clients use the destination |
| Data lost when the server dies | Everything since the last backup | (not a protection against it) | Everything since the last snapshot copied off | Everything since the last sync | Seconds (what was not sent yet) |
| Load on the server | Reads every blob on each run (incremental copy is planned, B5c) | One database snapshot, seconds | None | Lists, then copies what changed | Sends each change once |

A **backup never stops the server**: the metadata is read in one transaction while writes go on. A **restore always
does**: the data under a running server is never replaced.

## Which to use

| Setup | Recommended | Why |
|---|---|---|
| **One node, small data** (a VPS, a homelab, up to a few hundred GB, moderate writes) | **Full backups** nightly, archive copied off the machine + **Metadata-only backups** hourly with `RetainDeletedDays` + **Versioning** on important buckets | One file per backup, restore checked entry by entry; the metadata backups undo a mistake of the last hours in minutes |
| **Small team or small cluster** (TimeStore in Kubernetes next to your apps, uploads during the day) | **Replication** (or **Mirroring** hourly) to another S3 (R2, B2, another TimeStore) + **Metadata-only backups** + full backups nightly while the data stays small | Replication narrows the loss window to seconds; metadata backups keep clean points in time |
| **Large or busy** (TBs, continuous uploads, users worldwide) | **Replication** to a versioned destination, or **Volume snapshots** copied off the storage + **Metadata-only backups** for undo | A full backup reads all data each run and needs free space as large as the data; snapshots and replication do not grow with it, and a metadata backup stays a database's size |

Full backups on a large server: for one-off jobs (moving to another cluster, a copy before a major upgrade), streamed
(`/api/v1/backups/stream`) so the data volume needs no free space for it. Daily full backups of large data wait for the
incremental blob copy (B5c).

Always:

- Keep at least one copy **off the machine** (another provider or region) and one that cannot be overwritten from
  TimeStore's side (a versioned bucket, or object lock at the destination).
- Keep a copy of the **master key** apart from the data (`MasterKey` or `MasterKeyFile`; [backup.md](backup.md#the-master-key)).
- **Practice a restore** now and then (a side-by-side restore leaves the running server alone).
- After any restore, **set replication up again**: archives hold no replication rules, and the restore names the ones
  it dropped ([replication guide](user-guide/replication.md), "Good to know").

## Full backups

**Beta.** Backups and restore are tested end to end, but the API, the options and the archive format may still change
before 1.0. They are a point-in-time copy, not a mirror: for data you cannot lose, also replicate or mirror it to
another storage.

Built (stages B1 and B1.1). One archive per backup: `manifest.json`, the metadata snapshot, every referenced blob, a
`blobs.sha256` list; `zstd` or plain tar. Streamed over HTTP or written to `<DataPath>/backups` (the newest
`Backup:Keep` kept); restored with `TimeStore.Server restore` into an empty data directory, from a file or an
http(s) URL. On Kubernetes, `restore.archive` in the Helm chart restores an empty volume before the server starts.
**Suited for:** one-node and small installations, moving to another machine, a safety copy before an upgrade. **Not
suited (yet) for:** daily backups of large data, since every run reads all blobs (incremental blob copy is stage B5c).

Next stages (roadmap): backups page in the admin UI (B2), schedules and retention (B3), sending archives to S3 (B4),
incremental blob copy (B5c), encryption (B6).

## Metadata-only backups

**Beta**, built (stages B5a and B5b). With `Blobs:RetainDeletedDays` above 0, overwritten and deleted blobs stay on disk
for that many days. A backup with `{"content":"metadata"}` then archives the metadata alone (seconds, the size of the
database); `TimeStore.Server restore --metadata`, with the server stopped, puts it back on the data directory it came
from after checking that every blob it references is still there, and changes nothing if not. Everything written
after the backup is undone; objects deleted after it are back. On Kubernetes: `restore.metadataArchive` in the Helm
chart. **Suited for:** undoing mistakes on servers of any size, often (hourly). **Not suited for:** a lost disk (the
blobs are on it) or a move to another machine; keep backups for less time than the retention. Details:
[backup.md](backup.md#metadata-only-backups-undo-to-a-point-in-time).

## Versioning

Built. Turned on per bucket (S3 `PutBucketVersioning` or the admin UI); lifecycle rules expire old versions. **Suited
for:** every bucket where a delete or an overwrite would hurt. It lives on the same disk, so it never replaces a copy
elsewhere.

## Volume snapshots

Works today, without TimeStore: the metadata (`metadata.db` and its WAL) and the blobs are on the same volume, so one
snapshot of that volume holds both at one instant, as after a power cut, which SQLite recovers from. On Kubernetes, a
`VolumeSnapshot` of the data claim with a CSI driver that supports snapshots (Longhorn, EBS, Ceph RBD; k3s's default
`local-path` does not). **Suited for:** large servers, since a snapshot does not grow with the data. Keep in mind that
a snapshot usually lives on the same storage: copy it off (Longhorn backups to S3, cloud snapshots to another region)
to survive losing that storage. Details: [backup.md](backup.md#2-snapshot-the-volume).

## Mirroring

Works today with any S3 tool; tested with `rclone` 1.68 between two TimeStore servers on k3s. **Suited for:** small
and medium buckets, copying to any storage rclone supports (S3, Google Drive, SFTP…), a stopgap until replication.
**Not suited for:** millions of objects synced often (each run lists both sides), or a near-zero loss window. A mirror
holds the current objects (with their metadata when rclone runs with `-M`), not versions, bucket settings or access
keys. Details: [mirroring.md](mirroring.md).

## Replication

A per-bucket rule sends every new or changed object (and, if enabled, deletes) to another S3 endpoint (MinIO, AWS
S3, Cloudflare R2, Backblaze B2, another TimeStore). Changes are queued in the same transaction as the write, so none
is lost on a crash; the client is answered first and the copy follows within seconds. Objects already in the bucket
are copied when the rule is created. **Suited for:** busy servers, continuous uploads, large data, a standby server in
another place. Set up and watched from the bucket page (**⋯ → Replication**). Not yet: versions (only each key's current state is sent), a status header.
Guide: [user-guide/replication.md](user-guide/replication.md); design: [research/replication.md](research/replication.md).
