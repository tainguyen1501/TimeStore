# Online backup and restore (design; stage B1 built)

Sources: SQLite's Online Backup API and `VACUUM INTO` documentation; .NET's `System.Formats.Tar` and
`System.IO.Compression` (Zstandard is new in .NET 11); this repository's `docs/backup.md` (backups taken from outside:
stop and copy, volume snapshots) and `docs/architecture.md` (write path, immutable blobs, deferred blob deletion,
orphan sweep); the run model of the sibling platform TimeOps (a runs table as the source of truth); and the public
documentation of other S3 servers for what they offer (section 10).

Status: stage B1 (engine, `stream` and `directory` targets, admin API, `restore` command) is built and tested,
including on the Native AOT binary (section 15). B2–B6 are planned. Operators: `docs/backup.md`; API: `docs/api/admin.md`
"Backups"; table: `docs/database.md` `backup_runs`.

## 1. Goals

- **Back up a running server**: no stop, no lost requests while the backup runs.
- **Consistent at one instant T**: the archive holds exactly the buckets, objects, versions, keys and settings as they
  were at T, never metadata without its data or the reverse.
- **Verified**: every blob is checked against its stored checksum while it is copied; the archive carries a manifest
  with checksums, so a restore checks again.
- **One flow** for every way a backup starts: a click, a schedule, an API call, the hook of an outside tool.
- **Easy to extend**: new destinations, schedules, incremental backups and later several nodes without rewriting the
  engine.

Not goals: continuous replication (multi-node work), restoring into a running server (a restore always runs with the
server stopped).

## 2. Archive format: tar + Zstandard

One format, built only from the .NET base library (no package, Native AOT):

- **Container: tar** (`System.Formats.Tar`, `TarWriter`/`TarReader`, PAX entries). Tar is written strictly in order,
  so the archive streams into an HTTP response or an S3 multipart upload without seeking or holding an index in
  memory (zip needs its central directory at the end, one entry per blob). PAX has no size limit, and every system can
  read it.
- **Compression: Zstandard** (`System.IO.Compression.ZstandardStream`, .NET 11) around the whole tar stream, level 3 by
  default (`Backup:Compression` = `zstd` | `none`, `Backup:CompressionLevel` 1–19). The metadata snapshot, logs, JSON
  and text compress well; images, video and already compressed files pass through at little cost. `none` gives a
  plain `.tar` for the widest compatibility (Windows' built-in tools and plain 7-Zip do not read `.tar.zst`).
- **Names:** `timestore-<node>-<yyyyMMddTHHmmssZ>.tar.zst` (or `.tar`). The format is not a user choice; only the
  compression is. A restore detects it from the file's magic bytes, so nobody has to remember what was chosen.

Entries:

| Entry | Content |
|---|---|
| `manifest.json` (first) | Format version, TimeStore version, `NodeId`, instant T, counts (buckets, objects, versions, blobs, bytes), SHA-256 of the metadata snapshot, compression, whether the master key is included |
| `metadata.db` | The SQLite snapshot taken at T |
| `blobs/<xy>/<id>` | Every blob the snapshot references, same layout as the data directory |
| `blobs.sha256` (last) | One line per entry (`metadata.db`, every blob, `master.key`): SHA-256 and name in `sha256sum` format, so a restore, or `sha256sum -c` after `tar -xf`, verifies without the database |
| `master.key` | Only when asked for (default: not included; section 9) |

The layout mirrors the data directory, so a restore is mostly "unpack into an empty `DataPath`", and the archive opens
with ordinary tools: `tar --zstd -xf timestore-node-1-20261118T020000Z.tar.zst`.

## 3. Consistency: snapshot plus blob hold

1. **Hold blobs.** Before the snapshot the run takes a *blob hold* (`IBlobHold`). While any hold is active the
   deferred deletion queue deletes nothing (blobs released with no delay are queued instead) and drains when the last
   hold ends. That is enough: every committed blob is deleted through that queue, and the orphan sweep already skips
   blobs waiting in it, so a blob the snapshot references and a later commit releases cannot disappear. The sweep needs
   no change (an end-to-end test with no deletion delay fails without the hold and passes with it). Writes are not
   affected.
2. **Snapshot the metadata** with SQLite (`VACUUM INTO` a file under `<DataPath>/tmp/`, or the Online Backup API): one
   read transaction, so the copy is the database at T while the single writer keeps committing (WAL mode).
3. **List the blobs** the snapshot references: current and noncurrent versions, parts of open multipart uploads
   (a `UNION` over `objects`, `object_versions`, `object_parts`, `upload_parts` in the snapshot file). The snapshot is
   trimmed first: `admin_sessions` and `backup_runs` are emptied and it is switched to a rollback journal, so the
   archived file is self-contained and a restored server starts without sessions or history.
4. **Copy each blob** into the archive, verifying its checksum on the way. A mismatch fails the run and names the
   object; there is never a silently broken archive.
5. **Release the hold.** Deletions that waited run now.

Objects written after T are not in the archive. Blobs released during the backup stay on disk until the hold ends,
so disk use grows by what was overwritten or deleted meanwhile; a run refuses to start when free space is below
`Backup:MinFreeBytes`. After a crash the hold is gone with the process: in-memory deferred deletions are lost as
today and the orphan sweep removes unreferenced blobs on its next run, as it already does after a crash.

## 4. One flow: runs in the database

Every backup, however it starts, is a row before any work starts (the model TimeOps uses for its runs):

```
click "Back up" ─┐
schedule tick ───┼─▶ IBackupService.StartAsync(trigger, target, options)
API / hook ──────┘      1. validate; refuse when a backup is already active
                        2. insert a backup_runs row (Queued)
                        3. wake the worker (in-memory channel; the row is the truth)
                     BackupWorker ─▶ claim (Queued → Running) ─▶ engine ─▶ Succeeded | Failed | Cancelled
```

- **Table `backup_runs`** (metadata database, `docs/database.md`): id (UUIDv7), trigger (`manual`, `api`,
  `schedule`), target type, compression, include master key, status (`queued`, `running`, `succeeded`, `failed`,
  `cancelled`, `interrupted`), queued/started/finished at, instant T, counts and bytes done (progress), archive name,
  size and SHA-256, error code and message, who started it. Schedule and target ids come with B3 and B4.
- **At most one active run** (a filtered unique index on the active statuses).
- **On startup** rows left `Running` become `Interrupted` and are **never re-run automatically**; `Queued` rows are
  woken again, oldest first.
- **Cancel** ends a queued run at once; a running one is signalled and stops at its next blob; the partial archive is
  removed from the target. A run whose caller goes away (shutdown, a streaming client that disconnects) ends
  `interrupted`.
- **Progress** is written to the row every few seconds, not per blob; the UI polls the admin API.
- A streamed download (section 5) is a run too: same row, lock and history, executed inline by the request instead
  of the worker.

The engine (`IBackupEngine`) knows nothing about triggers, schedules or HTTP: it takes a run, a blob-hold provider, a
snapshot source, a blob reader and a target, and writes an archive.

## 5. Destinations: one interface, added per stage

```csharp
public interface IBackupTarget                       // every destination: what the engine needs
{
    string Type { get; }                             // "stream", "directory", "s3", ...
    ValueTask<IBackupUpload> OpenWriteAsync(string archiveName, CancellationToken ct);
}

public interface IBackupUpload : IAsyncDisposable    // disposed without commit: what was written is removed
{
    Stream Stream { get; }
    ValueTask CommitAsync(CancellationToken ct);     // the archive becomes visible under its name
}

public interface IBackupArchiveStore : IBackupTarget // destinations that keep archives (not "stream")
{
    ValueTask<IReadOnlyList<StoredBackupArchive>> ListAsync(CancellationToken ct);   // retention, the UI
    ValueTask<Stream?> OpenReadAsync(string archiveName, CancellationToken ct);       // download, restore
    ValueTask<bool> DeleteAsync(string archiveName, CancellationToken ct);           // retention
}
```

| Target | Where the archive lives | Stage |
|---|---|---|
| `stream` | Nowhere in TimeStore: written straight into the response of `GET /api/v1/backups/stream` (curl, a Velero pre-hook, TimeOps, a CronJob) | B1 ✅ |
| `directory` | `Backup:Path`, a directory **outside the data directory**: a second volume or a network share (archives are plain files, unlike the live SQLite database); written as `<name>.partial`, renamed when complete; a crash's `.partial` is removed at the next start | B1 ✅ |
| `s3` | Another S3 endpoint (AWS, Backblaze, another TimeStore…), multipart upload while the archive is written: no local copy | B4 |
| later | SFTP, Azure Blob, GCS: one class each | — |

**Temporary space inside TimeStore** is the metadata snapshot, `<DataPath>/tmp/backup-<run id>.db` (megabytes to a
few gigabytes), and the checksum list being collected, `tmp/backup-<run id>.sha256.tmp` (about 100 bytes per blob),
both deleted when the run ends and removed at startup after a crash. Blobs are
never copied twice locally. Archives are not kept in the data directory by default: a copy on the same disk does not
survive that disk. Targets are configured once (`backup_targets`, credentials encrypted with the master key like
access-key secrets) and referenced by id.

## 6. Schedules and retention (stage B3)

- **Table `backup_schedules`**: name, enabled, interval or daily time (UTC), target, compression, keep last N, include
  master key.
- **`BackupScheduler`** (a timer, once a minute) finds due schedules and calls the same `StartAsync(schedule, …)`; a
  schedule due while a backup runs is skipped and recorded, never queued twice.
- **Retention** runs after a successful scheduled backup: the target keeps the newest N archives of that schedule.

## 7. Restore

Restore runs with the server stopped, into an empty data directory:

```bash
TimeStore.Server restore --archive timestore-node-1-20261118T020000Z.tar.zst --data-path /data [--master-key /secrets/master.key]
```

It detects the compression, checks the manifest (format, a TimeStore version not newer than the binary), the metadata
SHA-256 and every blob checksum, unpacks, and refuses a non-empty data directory. The next start runs the migrations,
if any. The image and the Helm chart document a one-off restore (a Job or init container with the claim mounted). No
restore button in the UI: replacing a running server's data is not a click.

## 8. Native AOT

TimeStore ships only as a Native AOT binary, so the archive code must publish without trimming or AOT warnings on
linux-x64, linux-arm64 and osx-arm64. `System.Formats.Tar` is managed code. Zstandard calls the runtime's native
compression library (`System.IO.Compression.Native`), which Native AOT links statically on Linux. A spike (2026-11-18,
section 12) checked both on all three targets; CI's AOT job and the end-to-end tests on the AOT binary keep checking
it once B1 lands (backup, then restore, then compare).

## 9. Security

- Only admins start, list, download or delete backups; every action is logged (who, what, archive name).
- The **master key is not included by default**: an archive alone cannot decrypt access-key secrets. A backup asks for
  it explicitly; the manifest says whether it is in. Without it a restore needs the key kept apart (`docs/backup.md`).
- Archives are not encrypted in B1–B4 (the target's own encryption applies); archive encryption is stage B6.
- Target credentials are stored encrypted and never returned or logged.

## 10. What other S3 servers offer (public documentation, 2026-11)

| Server | Built-in backup |
|---|---|
| MinIO (community) | None. Copying elsewhere: `mc mirror` (current objects only, no versions or metadata), bucket replication, site replication |
| Garage | `garage meta snapshot` (metadata only, consistent while running, automatic interval, keeps two); object data is left to file-system snapshots |
| SeaweedFS | `weed backup` (incremental per volume; writes must pause for consistency); `weed filer.backup` (continuous push of changes to S3, GCS, Azure, B2 or local disk; incremental mode keeps changes by date and does not apply deletions) |

None of them offers a point-in-time archive of metadata **and** data taken while running, with schedules and
destinations in a UI. Immutable blobs make it cheap for TimeStore: the hold replaces any copy of the data at T.

## 11. Stages

| Stage | Content | Tests |
|---|---|---|
| **B1: engine** ✅ | Blob hold (deferred deletions wait for it; the sweep already skips what they hold), metadata snapshot, tar + Zstandard archive, checksum verification, `backup_runs` + `StartAsync` + `BackupWorker`, targets `stream` and `directory`, admin API (start, list, get, cancel, download), `restore` command | Backup while objects are written, overwritten and deleted, then restore: every object at T present with its checksum; `zstd` and `none`; crash during a backup leaves the server intact and deletions resume; a corrupted blob fails the run; one active run; interrupted runs not re-run; restore refuses a non-empty directory and a newer format; the same round trip on the AOT binary in CI |
| **B1.1: restore anywhere** | `restore --archive` from an `https://` URL or S3, `--if-empty` (skip a data directory that has data), `restore.archive` in the Helm chart as an init container: a restore is a Values change applied by TimeOps / Argo CD | Restore from a URL; a second start skips; the chart restores into an empty claim and never over data; on k3s through TimeOps |
| **B2: UI** | Backups page: start (target, compression, include master key), list with status and progress, download, cancel; user guide | Frontend checks; admin API covered in B1 |
| **B3: schedules** | `backup_schedules`, scheduler, retention, UI | A due schedule starts one run; a busy server skips it; retention keeps N |
| **B4: S3 target** | `s3` target with multipart upload, `backup_targets` with encrypted credentials, UI | Against a local TimeStore as the destination |
| **B5: incremental** | Blobs stored once per target (by id: blobs never change), each archive = manifest + metadata snapshot + only new blobs | Restore from an incremental chain equals a full backup |
| **B6: encryption** | Archives encrypted with a key the user keeps | Restore with and without the right key |

Docs per stage: `docs/backup.md` (operators), the user guide (from B2), the Helm chart README (restore Job).

## 12. AOT spike (2026-11-18)

A throwaway program (.NET 11.0.0, `PublishAot`, IL and ILC warnings as errors) wrote a tar.zst archive with `TarWriter`
(PAX) over `ZstandardStream` (quality 3, frame checksum on) into a **non-seekable stream** like an HTTP response:
`manifest.json`, an 8.2 MiB compressible "database", a 64 MiB random blob, a 1 KiB and an empty blob, 5.3 MiB of log
text in nested folders (77.5 MiB). It read the archive back with `TarReader` over a decompressing `ZstandardStream`,
compared every entry's SHA-256, then checked that an archive cut in half fails.

| Target | Native AOT publish | Round trip in .NET | Truncated archive | System tools |
|---|---|---|---|---|
| osx-arm64 (Apple M4) | no warnings, one 2.7 MB binary (Zstandard linked in) | 6/6 entries equal; 77.5 → 64.6 MiB; write 66 ms, read + verify 222 ms | `InvalidDataException` | bsdtar `tar --zstd -tvf` lists it |
| linux-arm64 (Docker Desktop) | no warnings; `ldd` shows no zstd or zlib library | 6/6 equal; write 106 ms, read + verify 326 ms | `InvalidDataException` | GNU `tar --zstd -tvf`, `zstd -t` OK |
| linux-x64 (4-vCPU VPS, Haswell) | no warnings; `ldd` shows no zstd or zlib library | 6/6 equal; write 496 ms (quality 3), 295 ms (1), 710 ms (9); size 64.6–64.8 MiB at every quality | `InvalidDataException` | GNU `tar --zstd -tvf`, `zstd -t` OK |

Conclusions: tar + Zstandard works under Native AOT on the three targets with no package and no native file to ship;
the format streams into a non-seekable writer; corruption is detected (the frame checksum and the tar structure); the
archives open with standard tools. Higher qualities cost time without gain on already compressed data, which supports
quality 3 as the default.

## 13. Several nodes later

With replication across nodes (roadmap phase 8) a backup needs coordination: one run for the cluster under a **lease**
(owner + expiry, `ILeaseProvider`, roadmap "Multi-node" item 8) instead of the in-process lock; blob holds on every
node holding a replica; the metadata snapshot from the shared metadata store (a database snapshot for Postgres); blobs
read from any replica. The single-node design keeps these seams: the engine takes the hold provider, snapshot source
and blob reader as interfaces, so the multi-node versions replace implementations, not the flow.

## 14. Decisions to review

| # | Proposal | Why |
|---|---|---|
| 1 | Every backup is a `backup_runs` row first, then the worker or the request runs it | One history, lock, progress and cancel; clicks, schedules and hooks share the code |
| 2 | One archive format, tar + Zstandard (level 3), compression `zstd` or `none` | Streams without seeking, no size limit, base library only, opens with ordinary tools; one format to restore and test |
| 3 | No archives in the data directory by default; targets `stream` and `directory` first | A copy on the same disk is not a backup; streaming needs no space |
| 4 | Master key excluded by default | A leaked archive should not carry the key to its secrets |
| 5 | Restore only offline, by command | Replacing a running server's data is not a click |
| 6 | Lease and multi-node coordination wait for phase 8 | Single node runs one process; the interfaces are ready |

## 15. Stage B1 as built (2026-11-18)

| Part | Where |
|---|---|
| `BackupRun`, statuses, triggers, targets, compressions | `Domain/Backups/BackupRun.cs` |
| Runs store, snapshots | `Domain/Stores/IBackupRunStore.cs`, `IMetadataSnapshots.cs`; SQLite: `SqliteMetadataStore.Backups.cs`, `SqliteMetadataSnapshots.cs`, migration v10 |
| Blob hold | `IBlobHold`, implemented by `DeferredBlobDeletions` |
| Archive format (tar, Zstandard, hashing, manifest JSON) | `IBackupArchiveFormat`; `Infra/Backups/TarBackupArchiveFormat.cs` |
| Engine, service, queue | `IBackupRunner` / `BackupRunner`, `IBackupService` / `BackupService`, `BackupQueue` |
| Targets | `DirectoryBackupTarget` (Infra), the HTTP stream target (`Server/Admin/AdminBackupApi.cs`) |
| Worker | `Server/Workers/BackupWorker.cs`: recovers at startup (running → interrupted, queued streams → interrupted, queued stored runs woken), then runs the queue |
| Restore | `Infra/Backups/BackupRestorer.cs`, `Server/RestoreCommand.cs` (`TimeStore.Server restore`) |

Tests: `BackupStoreContract` (runs and snapshots on SQLite); unit tests of the engine, service, restorer, layout and
the hold; end to end (`BackupApiTests`, `BackupFailureTests`, `RestoreCommandTests`): stream and restore with both
compressions (single, multipart and versioned objects, an access key, nothing after T), a backup during continuous
overwrites and deletes with no deletion delay, the directory target and download, a corrupted blob, a viewer refused,
restore refusals, and the crash criterion (`BackupCrashTests`): the process is killed with SIGKILL in the middle of a
run, after an overwrite whose old blob the hold kept; after the restart the run is `interrupted` with
`BACKUP_INTERRUPTED` and stays so, no `.partial`, snapshot or checksum list is left, every object reads back intact,
the orphan sweep reclaims exactly the held blob, and the next backup succeeds. Native AOT: osx-arm64 publish without
warnings, the whole end-to-end suite (262 tests) green on the binary; linux-arm64 container image built, backup (stream
and directory), `restore` in the container, a server on the restored volume returned every object byte for byte. CI
runs the end-to-end suite on the linux-x64 AOT binary.
