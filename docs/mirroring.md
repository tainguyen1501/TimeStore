# Mirroring with rclone

Copy TimeStore buckets to another storage on a schedule with [rclone](https://rclone.org) (MIT, one binary): another
TimeStore, MinIO, AWS S3, Cloudflare R2, Backblaze B2, or anything rclone supports (Google Drive, SFTP…). No TimeStore
feature is involved: rclone talks S3 to TimeStore like any client. Where this fits among the other mechanisms:
[data-protection.md](data-protection.md).

Tested 2026-11-19 with `rclone/rclone:1.68.2` as a Kubernetes Job, from a TimeStore to a second TimeStore on k3s:
first sync, `rclone check` (0 differences), a delete and an add on the source, sync again, check again; and an object
with `Cache-Control`, `Content-Disposition`, `Content-Encoding` and user metadata, synced without and with `-M`
(results in "What a mirror holds").

## Configure the two sides

rclone reads remotes from environment variables, so no config file is needed (handy in a container). TimeStore is a
generic S3 provider (`Other`) with path-style addressing, rclone's default for it.

```bash
export RCLONE_CONFIG_SRC_TYPE=s3 RCLONE_CONFIG_SRC_PROVIDER=Other
export RCLONE_CONFIG_SRC_ENDPOINT=https://s3.example.com            # TimeStore's S3 port
export RCLONE_CONFIG_SRC_ACCESS_KEY_ID=<key> RCLONE_CONFIG_SRC_SECRET_ACCESS_KEY=<secret>

export RCLONE_CONFIG_DST_TYPE=s3 RCLONE_CONFIG_DST_PROVIDER=Cloudflare   # or AWS, Minio, Other (another TimeStore)…
export RCLONE_CONFIG_DST_ENDPOINT=https://<account>.r2.cloudflarestorage.com
export RCLONE_CONFIG_DST_ACCESS_KEY_ID=<key> RCLONE_CONFIG_DST_SECRET_ACCESS_KEY=<secret>
```

Use an access key that may only read the source buckets (an access key with `s3Scope: policy` and a bucket policy
granting `s3:GetObject` and `s3:ListBucket`), not the root key.

## Copy or sync

```bash
rclone copy -M src:photos dst:photos-mirror   # adds and updates; never deletes on the destination
rclone sync -M src:photos dst:photos-mirror   # makes the destination equal: also deletes what the source deleted
rclone check src:photos dst:photos-mirror     # compares both sides (size and checksum); exit code 1 on differences
```

- **`copy`** keeps files deleted on TimeStore at the destination: a mistake on TimeStore is not copied over.
- **`sync`** mirrors deletes too. Pair it with **versioning on the destination** (or `--backup-dir` to a dated
  folder), or a bad delete on TimeStore removes the copy at the next run.
- Every run lists both buckets: fine for thousands to a few hundred thousand objects; with millions, sync less often
  or by prefix, and see replication (planned) in [data-protection.md](data-protection.md).
- All buckets at once: `rclone sync -M src: dst:` creates the destination buckets as needed.

## What a mirror holds

rclone sees TimeStore as any S3 client does: it copies **objects**, not the server's metadata database.

| | Mirror (rclone) | [Backup archive](backup.md) |
|---|---|---|
| Object content, `Content-Type` | ✅ | ✅ |
| `Cache-Control`, `Content-Disposition`, `Content-Encoding`, user metadata (`x-amz-meta-*`) | ✅ only with `-M` (`--metadata`) | ✅ |
| Older versions and delete markers | ❌ the current version only | ✅ |
| Bucket settings: versioning, lifecycle rules, policy, public folders | ❌ | ✅ |
| Access keys | ❌ | ✅ (usable with the master key) |
| Multipart uploads in progress | ❌ | ✅ |

What the test showed on the destination (same ETag in every case):

| Header | Source | `sync` without `-M` | `sync -M` |
|---|---|---|---|
| `Content-Type` | `text/plain; charset=utf-8` | kept | kept |
| `Cache-Control`, `Content-Disposition`, `Content-Encoding` | set | **lost** | kept |
| User metadata `owner`, `original-name` | set | **lost** | kept |
| Added by rclone | — | `x-amz-meta-mtime` | `x-amz-meta-mtime`, `x-amz-meta-btime` |

**Always pass `-M`** when your application stores anything in object metadata (an original file name, an owner, a
download file name in `Content-Disposition`): without it the mirror keeps the bytes and the type only.

## On Kubernetes: a CronJob

Credentials go in a Secret; the CronJob runs rclone with the same security settings as the TimeStore pod (non-root,
read-only root file system). With TimeStore in the same cluster, use its Service: `http://<release>-timestore-s3.<namespace>.svc:9000`.

```bash
kubectl -n timestore create secret generic timestore-mirror \
  --from-literal=RCLONE_CONFIG_SRC_TYPE=s3 --from-literal=RCLONE_CONFIG_SRC_PROVIDER=Other \
  --from-literal=RCLONE_CONFIG_SRC_ENDPOINT=http://timestore-s3.timestore.svc:9000 \
  --from-literal=RCLONE_CONFIG_SRC_ACCESS_KEY_ID=<key> --from-literal=RCLONE_CONFIG_SRC_SECRET_ACCESS_KEY=<secret> \
  --from-literal=RCLONE_CONFIG_DST_TYPE=s3 --from-literal=RCLONE_CONFIG_DST_PROVIDER=Cloudflare \
  --from-literal=RCLONE_CONFIG_DST_ENDPOINT=https://<account>.r2.cloudflarestorage.com \
  --from-literal=RCLONE_CONFIG_DST_ACCESS_KEY_ID=<key> --from-literal=RCLONE_CONFIG_DST_SECRET_ACCESS_KEY=<secret>
```

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: timestore-mirror
spec:
  schedule: "0 * * * *"            # hourly (UTC)
  concurrencyPolicy: Forbid        # never two syncs at once
  jobTemplate:
    spec:
      backoffLimit: 1
      template:
        spec:
          restartPolicy: Never
          securityContext: { runAsNonRoot: true, runAsUser: 65534, runAsGroup: 65534 }
          containers:
            - name: rclone
              image: rclone/rclone:1.68.2
              args: [sync, -M, "src:photos", "dst:photos-mirror", --stats-one-line, -v]
              envFrom: [{ secretRef: { name: timestore-mirror } }]
              env: [{ name: RCLONE_CONFIG, value: /tmp/rclone.conf }]   # rclone wants a writable place for it
              securityContext:
                allowPrivilegeEscalation: false
                readOnlyRootFilesystem: true
                capabilities: { drop: [ALL] }
              volumeMounts: [{ name: tmp, mountPath: /tmp }]
          volumes: [{ name: tmp, emptyDir: {} }]
```

On TimeOps: the Secret's values as workspace secrets in a `kind: Secret`, and the CronJob, together as a **Plain YAML**
application. Check a run with `kubectl -n timestore logs job/<job name>` (`Copied (new)`, `Deleted`, the totals).

## Restore from a mirror

A mirror gives back the **files**, not the server's configuration:

1. Use the mirror bucket directly: point the application at its endpoint and bucket, or
2. Start a new TimeStore, create the buckets, access keys, policies and lifecycle rules again, then copy back with the
   remotes reversed: `rclone copy -M dst:photos-mirror src:photos`.

For the whole server as it was (versions, settings, access keys), restore a [backup archive](backup.md). Use both: the
mirror loses at most one interval of files (an hour with the CronJob above), the nightly archive keeps settings and
history.

## MinIO Client (`mc mirror`)

`mc mirror` does the same (`mc mirror --watch` keeps running and copies changes as they come). TimeStore is not
tested with `mc` (no public image to run it in the compatibility suite); rclone is.
