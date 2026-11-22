# Backups

> **Beta.** Backups work and are tested, but how they are started and the backup file may still change before
> TimeStore 1.0. A backup is a copy of one moment, not a live copy: for data you cannot lose, also copy your buckets
> to another storage (`docs/mirroring.md`).

A backup is one file that holds the whole server as it was at one moment: buckets, objects, older versions, access
keys and settings. TimeStore takes it while it keeps serving requests, checks every object while it copies it, and
restores it with one command. This page walks through a first backup and a test restore; the details for operators
are in `docs/backup.md`.

Backups are started by programs (a script, a scheduled job, TimeOps), not from this UI: they call the backup API with
a key of their own.

## 1. Create a backup key

1. **Access Keys → Generate Access Key**.
2. Description: what will use it (`nightly backup`). S3 access: **Bucket policies only** (with no policy, the key
   cannot read buckets over S3). Role: **Backup**.
3. Copy the access key and the secret key now; the secret is never shown again.

A backup key can only call the backup API: it cannot sign in here.
It can still download every object inside an archive, so keep it where you keep other secrets. Use one key per
program, so that you can disable just that one.

## 2. Set up your shell

The backup API takes requests signed like S3 requests (AWS Signature Version 4), not the sign-in of this UI.
`curl` 7.75 or newer signs them with `--aws-sigv4`; the secret is used to sign and never sent.

```bash
ADMIN=http://127.0.0.1:9001        # the address of this admin UI
SIGN=(--aws-sigv4 "aws:amz:us-east-1:s3" --user "<access key>:<secret key>")
```

`us-east-1` is the region of the server (the `S3:Region` setting); use yours if it was changed. In zsh, paste the
commands below without the comments after `#`, or run `setopt interactivecomments` first.

## 3. Take a backup

**Download it now**, as it is written (nothing is kept on the server):

```bash
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/stream"
```

You get `timestore-node-1-<date>T<time>Z.tar.zst` in the current folder. `-f` makes `curl` fail if the backup fails
half way, so a partial file is never taken for a complete one. With a lot of data (many GB, millions of objects),
prefer the server's backup folder below: the backup does not depend on a long download through a proxy.

**Or let the server write it to its backup folder** (`backups/` in the data folder), then take it:

```bash
curl -s "${SIGN[@]}" -H 'Content-Type: application/json' -d '{}' "$ADMIN/api/v1/backups"
curl -s "${SIGN[@]}" "$ADMIN/api/v1/backups"
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/<id>/archive"
curl -s "${SIGN[@]}" -X DELETE "$ADMIN/api/v1/backups/<id>/archive"
```

The first answer says `"status":"queued"`; a little later (a few seconds per GB on a fast disk) the list shows
`"status":"succeeded"` with the `id`, the `archiveName`, the number of objects copied and the `archiveSha256` of the
file. The backup folder is on the same disk as the data, so it is not a safe place to keep backups: download the
file, store it somewhere else, then delete it from the server. The server keeps only the newest one anyway.
Download it later with `curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/<id>/archive"`.

Copy the file somewhere other than the server's disk (another machine, a cloud bucket): a copy on the same disk does
not survive that disk.

## 4. Check the file

```bash
shasum -a 256 timestore-node-1-*.tar.zst          # equals the run's archiveSha256
tar --zstd -tf timestore-node-1-*.tar.zst | head   # manifest.json, metadata.db, blobs/…
```

(If `tar` does not know zstd: `zstd -dc <file> | tar -tf - | head`.)

## 5. Try a restore, without touching the running server

A restore fills an **empty** folder; it never writes over data. Use the same TimeStore program as the server:

```bash
TimeStore.Server restore --archive timestore-node-1-<date>T<time>Z.tar.zst --data-path ~/timestore-restored
```

It prints the buckets, objects and versions it found and **all checksums verified**. Running it again into the same
folder is refused (`BACKUP_RESTORE_NOT_EMPTY`).

Then start a second server on that folder, on other ports, and look around:

```bash
TimeStore__DataPath=$HOME/timestore-restored TimeStore__S3Port=19000 TimeStore__AdminPort=19001 \
TimeStore__S3__RootAccessKey=<root key> TimeStore__S3__RootSecretKey=<root secret> TimeStore.Server
```

Open `http://127.0.0.1:19001` and sign in with the root key: the buckets and objects are those of the moment the
backup was taken. The restored server starts without sessions and without backup history. Stop it with Ctrl+C and
delete the folder when you are done.

**Access keys after a restore:** their secrets are encrypted with the server's master key, which is part of the
server's settings and never in a backup. Start the restored server with the same master key and every key works;
with another key the server does not start and says so. Keep a copy of the master key apart from your backups: without
it the objects still come back, but the access keys other than the root key must be created again.

To replace the data of the real server with a backup (after a lost disk, for example), stop it, restore into an empty
data folder, and start it on that folder: `docs/backup.md`, "Restore an archive". `--archive` also takes an `http(s)`
URL (a presigned link). On Kubernetes, set `restore.archive` in the Helm chart's values: the pod restores an empty data
volume before the server starts (the chart's README has the steps).

## Undo to a point in time (metadata-only backups)

If the server keeps overwritten and deleted data for a while (`Blobs:RetainDeletedDays`, set by whoever runs it), a
backup of the **metadata alone** is enough to undo mistakes: it is small and takes seconds, so it can run every hour.
Start one with `{"content":"metadata"}` instead of `{}` in step 3. To go back to it, the server is stopped and
`restore --metadata` puts the metadata back; it first checks that every file the backup needs is still on disk, and
changes nothing if not. Details: `docs/backup.md`, "Metadata-only backups". It is not a copy of your data: keep full
backups or replication for a lost disk.

**Replication after any restore:** backups hold no replication rules, so a restored server copies nothing to other
storage until they are set up again. The restore lists the rules it dropped; create each again with **Copy the objects
already in the bucket too** ([Replication](replication.md), "Good to know").

## Settings

Set by whoever runs the server (`appsettings.json`, or environment variables `TimeStore__Backup__…`):

| Setting | Default | Meaning |
|---|---|---|
| `Backup:Path` | `backups/` in the data folder | Where backups started with `POST /api/v1/backups` are written, to be downloaded. Usually left as is; set it to put archives on another disk |
| `Backup:Keep` | 1 | How many archives stay in that folder; older ones are deleted after a new backup |
| `Backup:Compression` | `zstd` | `zstd` (`.tar.zst`) or `none` (`.tar`). Photos, videos and zip files do not compress: with mostly such data, `none` saves CPU and the file is the same size |
| `Backup:MinFreeBytes` | 1 GiB | A backup does not start with less free space on the data disk (it also needs room for an archive as large as the data) |

## When something goes wrong

| Answer | What it means |
|---|---|
| `AUTH_SIGNATURE_REQUIRED` | The request is not signed: `"${SIGN[@]}"` is missing |
| `S3_AUTH_SIGNATURE_MISMATCH` | Wrong secret, or a region other than the server's |
| `S3_AUTH_INVALID_ACCESS_KEY` | Unknown or disabled key |
| `AUTH_FORBIDDEN` | The key's role is not Backup or Admin |
| `BACKUP_LOW_DISK_SPACE` | Not enough free space for the archive: free some, or download and delete the archive kept from last time |
| `BACKUP_ACTIVE` | A backup is already running; wait for it |
| `zsh: no matches found` | zsh read a comment as part of the command: paste without the `#` comments |
