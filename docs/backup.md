# Backup and restore

Everything TimeStore stores lives in one data directory, plus a master key and the configuration. Back these up
together, and take the copy in a way that sees the directory at one instant: copying it file by file while the server
is writing can produce a backup that does not restore.

## What to back up

| What | Where (default) | Why |
|---|---|---|
| Metadata | `<DataPath>/metadata.db`, with `metadata.db-wal` and `metadata.db-shm` while the server runs | Buckets, objects, versions, access keys, policies, lifecycle rules |
| Object data | `<DataPath>/blobs/` | The bytes of every object version, one immutable file per blob |
| Master key | `<DataPath>/master.key`, or the path in `MasterKeyFile` | Decrypts the stored secrets of access keys |
| Configuration | `appsettings.json`, environment variables, your container or service definition | The root key pair (`S3:RootAccessKey`, `S3:RootSecretKey`), `NodeId`, ports and the rest of [the settings](configuration.md) |

`DataPath` is `/data` in the container image and `./data` (relative to the working directory) otherwise. `<DataPath>/tmp/`
holds writes in flight and is emptied at startup: it does not need a backup.

**The master key matters.** If `master.key` is missing when the server starts, TimeStore creates a new one without
warning, and every access key created before can no longer be used (the root key pair still works, since it comes from
the configuration). Keep a copy of the key you can find again.

## Take a consistent backup

Pick one of these. All three give a copy that restores; the difference is how long the server stops.

### 1. Stop, copy, start

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

### 2. Snapshot the volume (recommended for production)

Take an atomic snapshot of the file system or disk that holds the data directory, with the server running: ZFS
(`zfs snapshot`), btrfs (`btrfs subvolume snapshot`), LVM (`lvcreate --snapshot`), a cloud disk snapshot, or a
Kubernetes `VolumeSnapshot` of the claim `data-<release>-timestore-0` (Helm chart).

A snapshot is as safe as a power cut, and TimeStore is built to survive one: an object's data is written and fsynced
before its metadata commit, SQLite runs in WAL mode with `synchronous=FULL`, and blob files are never changed in
place. A snapshot therefore always restores to the last committed state. Blobs written by uploads that had not
committed yet are removed by the orphan sweep after the restore (`BlobSweep` settings).

The data directory and the master key must be in the same snapshot, or the key must not have changed since.

### 3. Back up from a snapshot with a backup tool

To keep backups off the machine, deduplicated or encrypted (restic, borg, Kopia, Velero on Kubernetes), point the tool
at a mounted snapshot from option 2, never at the live data directory.

```bash
# Example with ZFS and restic
zfs snapshot tank/timestore@nightly
restic backup /tank/timestore/.zfs/snapshot/nightly
zfs destroy tank/timestore@nightly
```

## What not to do

- **Do not `cp` or `rsync` the live data directory.** The files are copied at different moments: `metadata.db` and
  its `-wal` file may not match, and objects written during the copy may have metadata without data or the reverse.
- **Do not keep the data directory on NFS or SMB.** SQLite's locking and fsync are not reliable on network file
  systems; use a local disk or block storage (including network block storage such as a cloud volume).
- **Do not rely on object versioning as a backup.** Versions protect against overwrites and deletes inside TimeStore,
  not against losing the disk.

## Restore

1. Stop the server if it runs.
2. Put the backup in place as the data directory (an empty `DataPath`, or a new volume):
   ```bash
   docker volume create timestore-data
   docker run --rm -v timestore-data:/data -v "$PWD":/backup alpine:3.20 \
     sh -c 'tar -xzf /backup/timestore-20261118-0200.tar.gz -C /data && chown -R 1654:1654 /data'
   ```
   The container runs as user `app` (UID 1654): the restored files must belong to it.
3. Make sure the master key is the one from the backup: `master.key` in the data directory, or the file
   `MasterKeyFile` points to.
4. Start the server with the same configuration: the same root key pair, and the same `NodeId` (default `node-1`).
5. Check: sign in to the admin UI, list the buckets, download an object you know, and sign in with an access key
   other than the root key (this proves the master key is right).

A backup restores into the same TimeStore version or a newer one; newer versions migrate the metadata at startup.
Restoring into an older version is not supported.

## Kubernetes (Helm chart)

Volume snapshots (option 2) are the easiest way when your CSI driver supports them. Without snapshots, the stop, copy,
start way works with plain `kubectl`; these steps were tested end to end on k3s (backup, `helm uninstall`, claim
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
and their secrets come back with the volume (or with `masterKey.existingSecret`).

## Before you upgrade

Take a backup before you start a new TimeStore version. Schema migrations run automatically at startup and only
forward: the backup is how you go back.

## Keep the master key apart

By default `master.key` sits in the data directory, so every backup of the data also carries the key that decrypts
the access-key secrets in it. For production, move the key out of the data directory and back it up separately:

```bash
# Kubernetes secret, Docker secret or a file only the service user can read
TimeStore__MasterKeyFile=/run/secrets/timestore-master-key
```

The key is 32 random bytes; TimeStore creates it at the first start if the file does not exist, and refuses to start
if the file exists with another length. Store the copy where the data backups are not (a password manager or a
secrets vault).
