# Data protection

How to keep TimeStore's data safe: four mechanisms, what each protects against, and which to use for your size.
Built ones are documented in their own pages; planned ones are marked **Soon** and linked to the roadmap.

| Mechanism | What it is | Status | Page |
|---|---|---|---|
| **Backups** | A point-in-time archive of the whole server (metadata and data at one instant, every blob verified), taken while it runs; restored with a command | 🧪 Beta | [backup.md](backup.md) |
| **Versioning** | Older versions of every object are kept, so a delete or an overwrite can be undone | ✅ Built | [user guide](user-guide/README.md) |
| **Mirroring** | An outside tool (`rclone sync`) copies buckets to another storage on a schedule | ✅ Works today, no TimeStore feature needed | [mirroring.md](mirroring.md) |
| **Replication** | TimeStore itself sends every change of a bucket to another S3 endpoint, right after the commit | ✅ Built (admin UI and API) | [guide](user-guide/replication.md), [design](research/replication.md) |

TimeStore runs as **one server** (one pod on Kubernetes, whatever the cluster's size); several TimeStore nodes sharing
data come with phase 8 of the roadmap. "Small" and "large" below are about the data and the write rate, not the number
of Kubernetes nodes.

## What each one protects against

| Event | Backups | Versioning | Mirroring | Replication |
|---|---|---|---|---|
| Disk, VPS or cluster lost | ✅ (archive kept elsewhere) | ❌ (same disk) | ✅ | ✅ |
| Object deleted or overwritten by mistake | ✅ back to the last backup | ✅ any older version | ⚠️ only with `rclone copy` or a versioned destination | ⚠️ only with deletes off or a versioned destination |
| Bad writes or ransomware on many objects | ✅ an older archive is untouched | ✅ if versions are kept long enough | ❌ the next sync copies the damage | ❌ copied within seconds, unless the destination keeps versions |
| Whole server back to a given day | ✅ | ❌ (per object only) | ❌ | ❌ |
| Move to another machine or cluster | ✅ one file | ❌ | ✅ bucket by bucket | ✅ |

## How much you lose, how fast you are back

| | Backups | Mirroring | Replication |
|---|---|---|---|
| Data lost when the server dies | Everything since the last backup (up to the interval: 24 h for nightly) | Everything since the last sync | Seconds (what was not sent yet) |
| Back in service | Restore (unpack and verify), then start a server | Point clients at the copy (an S3 endpoint) | Point clients at the destination |
| Load on the server | Reads every blob on each run (full backup; incremental is planned) | Lists both sides on each run, copies what changed | Sends only what changed, as it changes |

## Which to use

| Setup | Recommended | Why |
|---|---|---|
| **One node, small data** (a VPS, a homelab, up to a few hundred GB, moderate writes) | **Backups** nightly, archive copied off the machine + **Versioning** on important buckets | One file per backup, restore checked entry by entry; versions undo mistakes without a restore |
| **Small team or small cluster** (TimeStore in Kubernetes next to your apps, uploads during the day) | **Backups** nightly + **Mirroring** hourly to another S3 (R2, B2, another TimeStore) + **Versioning**; or **Replication** | Mirroring narrows the loss window to an hour today, without new code; backups keep a clean point in time |
| **Large or busy** (TBs, continuous uploads, users worldwide) | **Replication** to a versioned destination + **Backups** weekly (incremental planned) | A full backup reads all data every time and a sync lists every object: neither scales to continuous writes; replication sends each change once |

Always:

- Keep at least one copy **off the machine** (another provider or region) and one that cannot be overwritten from
  TimeStore's side (a versioned bucket, or object lock at the destination).
- Keep a copy of the **master key** apart from the data (`MasterKey` or `MasterKeyFile`; [backup.md](backup.md#the-master-key)).
- **Practice a restore** now and then (a side-by-side restore leaves the running server alone).

## Backups

**Beta.** Backups and restore are tested end to end, but the API, the options and the archive format may still change before 1.0. They are a point-in-time copy, not a mirror: for data you cannot lose, also mirror it to another storage ([mirroring.md](mirroring.md)).

Built (stages B1 and B1.1). One archive per backup: `manifest.json`, the metadata snapshot, every referenced blob, a
`blobs.sha256` list; `zstd` or plain tar. Streamed over HTTP or written to a directory; restored with
`TimeStore.Server restore` into an empty data directory. **Suited for:** one-node and small installations,
point-in-time recovery, moving to another machine, a safety copy before an upgrade. **Not suited (yet) for:** large
data with constant writes, since every run reads all blobs (incremental backups are stage B5).

Restores read a file or an http(s) URL; on Kubernetes, `restore.archive` in the Helm chart restores an empty volume
before the server starts. Next stages (roadmap): schedules and retention (B3), sending archives to S3 (B4), incremental
backups (B5), encryption (B6).

## Versioning

Built. Turned on per bucket (S3 `PutBucketVersioning` or the admin UI); lifecycle rules expire old versions. **Suited
for:** every bucket where a delete or an overwrite would hurt. It lives on the same disk, so it never replaces a copy
elsewhere.

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
