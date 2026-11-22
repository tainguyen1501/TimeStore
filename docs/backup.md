# Backup and restore

> **Beta.** Backups and restore are tested end to end, but the API, the options and the archive format may still change before 1.0. They are a point-in-time copy, not a mirror: for data you cannot lose, also mirror it to another storage ([mirroring.md](mirroring.md)).

Backups are one of four ways to protect TimeStore's data; [data-protection.md](data-protection.md) compares them and
says which to use for your size.

Everything TimeStore stores lives in one data directory; the master key and the rest of the configuration are yours. The
[built-in backup](#built-in-backup-the-server-keeps-running) archives a running server consistently; a file-level copy
must see the directory at one instant: copying it file by file while the server is writing can produce a backup that
does not restore.

## What to back up

| What | Where (default) | Why |
|---|---|---|
| Metadata | `<DataPath>/metadata.db`, with `metadata.db-wal` and `metadata.db-shm` while the server runs | Buckets, objects, versions, access keys, policies, lifecycle rules |
| Object data | `<DataPath>/blobs/` | The bytes of every object version, one immutable file per blob |
| Master key | Your configuration: `MasterKey`, or the file in `MasterKeyFile` (never in the data directory) | Decrypts the stored secrets of access keys |
| Configuration | `appsettings.json`, environment variables, your container or service definition | The root key pair (`S3:RootAccessKey`, `S3:RootSecretKey`), `NodeId`, ports and the rest of [the settings](configuration.md) |

`DataPath` is `/data` in the container image and `./data` (relative to the working directory) otherwise. `<DataPath>/tmp/`
holds writes in flight and is emptied at startup: it does not need a backup.

**The master key matters.** Backups never hold it: keep a copy apart from the data ([The master key](#the-master-key)).
Without it the data restores, but every access key other than the root key pair must be created again.

## Built-in backup (the server keeps running)

TimeStore can back itself up while it serves requests. A backup is one archive that holds the server exactly as it was
at one instant T: the metadata, every blob it references (current and older versions, parts of open multipart
uploads), a manifest and a checksum list. Every blob is checked against its stored MD5 while it is copied, so an
archive is never silently broken. Writes go on during the backup; objects written after T are simply not in it.

The archive is a tar file, compressed with Zstandard by default (`.tar.zst`; `none` gives a plain `.tar`):

```
manifest.json                 what the archive holds (instant T, counts, versions, metadata checksum)
metadata.db                   the metadata snapshot at T
blobs/<xy>/<id>               the object data, same layout as the data directory
blobs.sha256                  checksum of every entry above (sha256sum format), last
```

### Who can back up: signed requests, a backup key

The backup API (`/api/v1/backups`) is for **programs** (a CronJob, TimeOps, a script), not for the admin UI: every
request is signed with AWS Signature Version 4, exactly like an S3 request, by an access key with the **`backup`**
role (or `admin`). Sign-in cookies are refused, so a browser cannot run backups.

- Create a key for each program that backs up: **Access keys → Create**, role **Backup**, S3 scope **Policy only**
  (with no bucket policy it has no S3 access at all). A backup key calls the backup API only; it cannot sign in to the
  admin UI. Keep it in a secret store: an archive holds all the data.
- The secret key never travels: the client signs each request with it (valid for 15 minutes), as the AWS CLI does.
  `curl` 7.75 or newer signs with `--aws-sigv4`; the AWS SDKs sign any HTTP request too.

The master key is never in an archive: an archive alone cannot decrypt the access-key secrets.

### Download a backup as a stream

Nothing is kept on the server: the archive is written straight into the response.

```bash
ADMIN=http://127.0.0.1:9001
# The backup API takes requests signed like S3 requests (SigV4), not sign-in cookies. Use a key with the
# "backup" role (or an admin key); curl signs with --aws-sigv4 (region: TimeStore:S3:Region, default us-east-1).
SIGN=(--aws-sigv4 "aws:amz:us-east-1:s3" --user "<access key>:<secret key>")
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/stream"                      # timestore-node-1-20261118T020000Z.tar.zst
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/stream?compression=none"     # a plain .tar
```

If the backup fails after it started, the connection is cut (`curl -f` exits non-zero): a partial download never
looks complete, and a restore refuses it.

**Large servers:** a stream sends its first byte only after the metadata snapshot, which takes from seconds to minutes
with millions of objects; proxies that limit the wait for a response (ingress-nginx `proxy-read-timeout`, 60 s by
default; Cloudflare, about 100 s) can cut it, and `kubectl port-forward` breaks easily on long transfers. For large
data, back up to a directory (`POST /api/v1/backups` answers at once and the server works in the background, with no
time limit), restore from there, and copy archives off the machine from that directory. A signature is checked when a
request starts, so a download that runs for hours is fine.

### Back up to the backup folder

Start a backup from the admin API; the server's backup worker writes the archive to `<DataPath>/backups`
(`TimeStore:Backup:Path` moves it), as `<name>.partial` renamed when complete. The name is
`timestore-<node>-<yyyyMMddTHHmmssZ>.tar.zst`; a later archive of the same second (a metadata-only backup takes
milliseconds) gets `-2`, `-3`, ... before the extension. The folder is readable by the server's user only. It is where archives are **taken from**, not where they are kept: it sits on the data disk, so it does not
survive the disk. Download the archive, keep it elsewhere, then delete it:

```bash
curl -s "${SIGN[@]}" -H 'Content-Type: application/json' -d '{}' "$ADMIN/api/v1/backups"
curl -s "${SIGN[@]}" "$ADMIN/api/v1/backups"                         # status, progress, archiveName, archiveSha256
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/<id>/archive"       # take it
curl -s "${SIGN[@]}" -X DELETE "$ADMIN/api/v1/backups/<id>/archive"   # free the space (the run stays in the history)
```

Only the newest `Backup:Keep` archives stay (default 1): after a backup succeeds, older ones are deleted. While a
backup runs, the disk needs room for the data plus an archive as large (uncompressed, the worst case): a backup that
cannot fit is refused before it writes anything (`BACKUP_LOW_DISK_SPACE`). The archive never holds the backup folder
(a backup copies the metadata and the blobs it references, nothing else); tools that copy the whole data directory
(restic, Kopia, rsync after a stop) can skip `backups/`.

One backup runs at a time. A backup that is running when the server stops (or crashes) is marked `interrupted` and is
not started again: start a new one. What it left (the `.partial` file, its snapshot in `<DataPath>/tmp/`) is removed at
the next start, and the blobs it kept from deletion are reclaimed by the orphan sweep. Blobs that writes release
during a backup stay on disk until it ends, so a backup refuses to start when the data directory has less than
`Backup:MinFreeBytes` free (1 GiB by default). Settings: [configuration](configuration.md).

A walkthrough for users, with the answers to expect at each step (a backup key, a first backup, checking the file, a
test restore into a second server): [user guide, Backups](user-guide/backups.md).

### Restore an archive

Restore runs with the server stopped, into an **empty** data directory (a `lost+found` left by ext4 is ignored). It checks the manifest (it refuses an archive
from a newer TimeStore), the metadata checksum and every entry against `blobs.sha256`, and removes everything it
wrote if any check fails:

```bash
TimeStore.Server restore --archive timestore-node-1-20261118T020000Z.tar.zst --data-path /srv/timestore/data
# Container: same image, the new volume at /data, the archive mounted read-only
docker run --rm -v timestore-data:/data -v "$PWD":/backup:ro ghcr.io/tainguyen1501/timestore:<version> \
  restore --archive /backup/timestore-node-1-20261118T020000Z.tar.zst
```

`--archive` takes a file or an `http(s)://` URL, read as it downloads (a presigned S3 or R2 URL: its query is a
credential and is never printed). `--if-empty` leaves a data directory that already holds data alone and exits with 0:
what the Helm chart's `restore.archive` runs in an init container at every start. While a restore writes, the data
directory holds `.timestore-restore`; a restore cut half way leaves it, the next restore starts over, and the server
refuses to start on such a directory.

`--data-path` defaults to `TimeStore:DataPath` (`/data` in the image). Then start the server with the same
configuration: the master key the access keys were encrypted with (a server with another key refuses to start and
says so), the root key pair, `NodeId`. The restored server has no admin sessions, no backup history and no replication rules (create them again on purpose, so an older state is never pushed over a newer replica); sign in again.

An archive of an earlier version may hold the master key (those versions could put it in): the restore writes it to
`master.key` in the data directory and says so. Move it out of the data directory and set `MasterKeyFile` to it, or
set `MasterKey` to `base64 < master.key`.

The archive also opens with ordinary tools: `tar --zstd -xf <archive>` gives a data directory, and
`sha256sum -c blobs.sha256` inside it checks it.

On Kubernetes, set `restore.archive` in the chart's values to the archive's URL (it must come from outside the data
volume, which a restore starts empty): an empty data claim is restored before the server starts, a claim with data is
left alone. The Helm chart's README has the steps (a new cluster, in place, side by side) and the
manual way with a Job
([deploy/helm/timestore/README.md](../deploy/helm/timestore/README.md)).

### Metadata-only backups (undo to a point in time)

With `Blobs:RetainDeletedDays` above 0, overwritten and deleted blobs stay on disk for that many days, so a backup of
the **metadata alone** can put the server back at its instant: a small file, taken in seconds, as often as you like
(hourly, for example). It protects against mistakes on a server whose disk is intact (a bucket deleted by error, an
application that overwrote everything); it is not a copy of the data, so a lost disk still needs a full archive or
[replication](user-guide/replication.md). Keep metadata backups for less time than the retention (kept 30 days:
`RetainDeletedDays` 35).

```bash
curl -s "${SIGN[@]}" -H 'Content-Type: application/json' -d '{"content":"metadata"}' "$ADMIN/api/v1/backups"
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/stream?content=metadata"      # or streamed
```

Refused with `BACKUP_RETENTION_REQUIRED` while the retention is 0. The archive holds `manifest.json` (with
`"content": "metadata"`, the blobs it references and the retention), `metadata.db` and `blobs.sha256`; it holds no
blob, and taking it holds no blob deletion.

**Restore it** onto the data directory it came from, with the server stopped:

```bash
TimeStore.Server restore --metadata --archive timestore-node-1-20261120T030000Z.tar.zst --data-path /srv/timestore/data
```

1. It refuses while a server runs on the directory (`BACKUP_RESTORE_IN_USE`: the server holds `.timestore-lock`),
   and refuses a full archive (`BACKUP_RESTORE_WRONG_CONTENT`).
2. It checks the archive, then **every blob the restored metadata references** (present, same size). When some are
   gone (deleted after the retention) it names them and changes nothing (`BACKUP_RESTORE_MISSING_BLOBS`);
   `--allow-missing` restores anyway, and those objects fail to read.
3. It moves the current `metadata.db` (with its `-wal` and `-shm`) to `metadata-before-restore-<time>/` (`-2`, ... for a
   second restore of the same second) in the data directory, puts the restored one in place and records the archive in `.timestore-restored-metadata`. Delete the
   `metadata-before-restore-…` folder once the server is checked.

Everything written after the backup's instant is undone: objects written later are gone (their blobs are orphans the
sweep removes), objects deleted later are back. `--once` skips an archive already restored there: what the Helm
chart's `restore.metadataArchive` runs in an init container. While it swaps, the directory holds
`.timestore-restore-metadata`; a restore cut there is run again, and the server refuses to start meanwhile. Like a
full restore, it brings back no admin session, backup history or replication rule.

## Copy the data directory yourself

The built-in backup is the simplest way. File-level copies also work, as long as they see the data directory at one
instant.

### Take a consistent copy

Pick one of these. All three give a copy that restores; the difference is how long the server stops.

#### 1. Stop, copy, start

The simplest way, with a short stop.

```bash
# Container with the named volume timestore-data
docker stop timestore
docker run --rm -v timestore-data:/data:ro -v "$PWD":/backup alpine:3.20 \
  tar -czf /backup/timestore-$(date +%Y%m%d-%H%M).tar.gz -C /data .
docker start timestore
```

```bash
# Binary or service: stop it with your service manager, then
tar -czf timestore-$(date +%Y%m%d-%H%M).tar.gz -C /srv/timestore/data .
```

#### 2. Snapshot the volume

Take an atomic snapshot of the file system or disk that holds the data directory, with the server running: ZFS
(`zfs snapshot`), btrfs (`btrfs subvolume snapshot`), LVM (`lvcreate --snapshot`), a cloud disk snapshot, or a
Kubernetes `VolumeSnapshot` of the claim `data-<release>-timestore-0` (Helm chart).

A snapshot is as safe as a power cut, and TimeStore is built to survive one: an object's data is written and fsynced
before its metadata commit, SQLite runs in WAL mode with `synchronous=FULL`, and blob files are never changed in
place. A snapshot therefore always restores to the last committed state. Blobs written by uploads that had not
committed yet are removed by the orphan sweep after the restore (`BlobSweep` settings).

The master key is not in the snapshot: the restored server needs the key of its configuration.

#### 3. Back up from a snapshot with a backup tool

To keep backups off the machine, deduplicated or encrypted (restic, borg, Kopia, Velero on Kubernetes), point the tool
at a mounted snapshot from option 2, never at the live data directory.

```bash
# Example with ZFS and restic
zfs snapshot tank/timestore@nightly
restic backup /tank/timestore/.zfs/snapshot/nightly
zfs destroy tank/timestore@nightly
```

### What not to do

- **Do not `cp` or `rsync` the live data directory.** The files are copied at different moments: `metadata.db` and
  its `-wal` file may not match, and objects written during the copy may have metadata without data or the reverse.
- **Do not keep the data directory on NFS or SMB.** SQLite's locking and fsync are not reliable on network file
  systems; use a local disk or block storage (including network block storage such as a cloud volume).
- **Do not rely on object versioning as a backup.** Versions protect against overwrites and deletes inside TimeStore,
  not against losing the disk.

### Restore a copy

1. Stop the server if it runs.
2. Put the backup in place as the data directory (an empty `DataPath`, or a new volume):
   ```bash
   docker volume create timestore-data
   docker run --rm -v timestore-data:/data -v "$PWD":/backup alpine:3.20 \
     sh -c 'tar -xzf /backup/timestore-20261118-0200.tar.gz -C /data && chown -R 1654:1654 /data'
   ```
   The container runs as user `app` (UID 1654): the restored files must belong to it.
3. Configure the master key the access keys were encrypted with (`MasterKey` or `MasterKeyFile`); with another key
   the server refuses to start and says so.
4. Start the server with the same configuration: the same root key pair, and the same `NodeId` (default `node-1`).
5. Check: sign in to the admin UI, list the buckets, download an object you know, and sign in with an access key
   other than the root key (this proves the master key is right).

A backup restores into the same TimeStore version or a newer one; newer versions migrate the metadata at startup.
Restoring into an older version is not supported.

## Kubernetes (Helm chart)

The built-in backup works the same on Kubernetes (the chart README, "Back up" and "Restore a backup"). For file-level copies, volume snapshots (option 2) are the easiest way when your CSI driver
supports them. Without snapshots, the stop, copy, start way works with plain `kubectl`; these steps were tested end to end on k3s (backup, `helm uninstall`, claim
deleted, restore, data and access keys back). Replace `ts` with your release name and `storage` with the namespace.

**Back up** (the S3 API is down for the copy):

```bash
kubectl -n storage scale statefulset ts-timestore --replicas=0
kubectl -n storage wait --for=delete pod/ts-timestore-0 --timeout=120s
helper() {   # a pod with the data claim mounted at /data
  kubectl -n storage apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata: { name: $1 }
spec:
  restartPolicy: Never
  containers:
    - name: c
      image: alpine:3.20
      command: ["sleep", "3600"]
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes: [{ name: data, persistentVolumeClaim: { claimName: data-ts-timestore-0 } }]
EOF
  kubectl -n storage wait --for=condition=Ready pod/$1 --timeout=120s
}
helper backup
kubectl -n storage exec backup -- tar -czf - -C /data . > timestore-$(date +%Y%m%d-%H%M).tgz
kubectl -n storage delete pod backup
kubectl -n storage scale statefulset ts-timestore --replicas=1
```

**Restore** into a new cluster or after the claim was lost: create the claim under the StatefulSet's name before you
install, fill it, then install with the same release name.

```bash
kubectl -n storage apply -f - <<EOF
apiVersion: v1
kind: PersistentVolumeClaim
metadata: { name: data-ts-timestore-0 }
spec:
  accessModes: [ReadWriteOnce]
  resources: { requests: { storage: 20Gi } }
EOF
helper restore
kubectl -n storage exec -i restore -- sh -c 'tar -xzf - -C /data && chown -R 1654:1654 /data' < timestore-20261118-0200.tgz
kubectl -n storage delete pod restore
helm install ts oci://ghcr.io/tainguyen1501/charts/timestore --version 0.1.0 -n storage -f values.yaml
```

Without `rootCredentials.existingSecret` the reinstall generates a new root secret; the data, the other access keys
and their secrets come back with the volume, as long as the master key is the same (`masterKey.existingSecret` or
`masterKey.value`).

## Before you upgrade

Take a backup before you start a new TimeStore version. Schema migrations run automatically at startup and only
forward: the backup is how you go back.

## The master key

The master key encrypts the secrets of stored access keys (not the objects). It is configuration, like the root key
pair, and TimeStore never creates one: without it the server does not start.

```bash
openssl rand -base64 32                                   # create it once
TimeStore__MasterKey=<the 44 characters>                  # a value, or
TimeStore__MasterKeyFile=/run/secrets/timestore-master-key # a file: Kubernetes Secret, Docker secret (Base64 or 32 raw bytes)
```

- Set one of the two, not both. The server refuses a key that is not 32 bytes, and a key that does not decrypt the
  stored access keys ("the master key does not decrypt N of M stored access keys"): it never starts with a new key
  that would leave them unusable.
- Keep a copy where the data backups are not (a password manager, a secrets vault). Backups never hold it.
- Losing it loses no data: the objects restore without it, and the access keys other than the root key pair must be
  created again.
- **Upgrading from a version that created `<data>/master.key`:** move that file out of the data directory and set
  `MasterKeyFile` to it, or set `MasterKey` to `base64 < master.key`. Until then the server refuses to start and names
  the file.
