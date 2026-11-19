# TimeStore Helm chart

Runs [TimeStore](https://github.com/tainguyen1501/TimeStore), a self-hosted S3-compatible object storage server, as
one pod with a persistent volume. TimeStore is single node today: the chart always runs one replica, and a
StatefulSet makes sure two pods never write the same volume.

## Install

```bash
openssl rand -base64 32 > timestore-master.key   # once: the master key, keep a copy outside the cluster
helm install timestore oci://ghcr.io/tainguyen1501/charts/timestore --version 0.1.0 \
  --namespace storage --create-namespace --set masterKey.value="$(cat timestore-master.key)"
```

The master key (32 bytes as Base64) encrypts the stored access-key secrets and is required: the chart refuses to
render without `masterKey.value` or `masterKey.existingSecret`, and TimeStore never creates one. Requires Kubernetes
1.25+ and Helm 3.8+ (OCI charts). After the install, Helm prints how to read the generated root
key pair and how to reach the S3 API and the admin UI. Check the installation with `helm test timestore -n storage`:
it creates a bucket, writes, reads and deletes an object through the S3 service.

From a clone of the repository: `helm install timestore ./deploy/helm/timestore -n storage --create-namespace`.

## Typical values

```yaml
# values-prod.yaml
rootCredentials:
  existingSecret: timestore-root        # keys: accessKey, secretKey
masterKey:
  existingSecret: timestore-master-key  # key: master.key (Base64, from openssl rand -base64 32)
persistence:
  storageClass: local-path              # local or block storage, never NFS or SMB
  size: 500Gi
ingress:
  s3:
    enabled: true
    host: s3.example.com
    tlsSecret: s3-example-com-tls
    annotations:                        # ingress-nginx: no body limit for large uploads
      nginx.ingress.kubernetes.io/proxy-body-size: "0"
      nginx.ingress.kubernetes.io/proxy-request-buffering: "off"
  admin:
    enabled: true
    host: storage.example.com
    tlsSecret: storage-example-com-tls
```

```bash
kubectl -n storage create secret generic timestore-root \
  --from-literal=accessKey=admin --from-literal=secretKey="$(openssl rand -base64 30)"
kubectl -n storage create secret generic timestore-master-key \
  --from-literal=master.key="$(openssl rand -base64 32)"
# keep a copy of the key outside the cluster (docs/backup.md, "The master key")
helm install timestore oci://ghcr.io/tainguyen1501/charts/timestore --version 0.1.0 -n storage -f values-prod.yaml
```

With the S3 ingress enabled, `S3:PublicUrl` follows its host (`https://` when it has TLS), so links in the admin UI
work from outside. With TLS on the admin ingress, the session cookie is marked `Secure`.

## Values

| Key | Default | Meaning |
|---|---|---|
| `image.repository` / `image.tag` | `ghcr.io/tainguyen1501/timestore` / appVersion | Container image |
| `rootCredentials.existingSecret` | `""` | Secret with the root key pair (`accessKeyKey`, `secretKeyKey` name its keys) |
| `rootCredentials.accessKey` / `secretKey` | `admin` / `""` | Used without `existingSecret`; an empty `secretKey` is generated (32 characters), kept across upgrades, new after a reinstall |
| `masterKey.value` | `""` | **Required unless `existingSecret`**: the master key as Base64 (`openssl rand -base64 32`), kept in a Secret of the chart. From TimeOps: `${{ secrets.TIMESTORE_MASTER_KEY }}` |
| `masterKey.existingSecret` / `key` | `""` / `master.key` | A Secret you manage with the master key (Base64, or the 32 raw bytes of an earlier `master.key`) |
| `config.region` | `us-east-1` | `S3:Region` |
| `config.publicUrl` | `""` | `S3:PublicUrl`; empty: from `ingress.s3` |
| `config.domains` | `[]` | `S3:Domains`, for virtual-host addressing |
| `config.secureCookies` | `null` | `Admin:SecureCookies`; null: true when `ingress.admin` has TLS |
| `config.sessionHours` | `24` | `Admin:SessionHours` |
| `backup.keep` | `1` | `Backup:Keep`: archives kept in `/data/backups` (1–100); older ones are deleted after a backup succeeds |
| `backup.compression` / `compressionLevel` / `minFreeBytes` | `zstd` / `3` / `1073741824` | `Backup:Compression`, `Backup:CompressionLevel` (1–19), `Backup:MinFreeBytes` |
| `restore.archive` | `""` | Restore this archive into the data volume when it is empty, before the server starts (an init container); an http(s) URL (or a file path the pod can read). See "Restore a backup" |
| `extraEnv` | `[]` | More settings as `TimeStore__Section__Key` variables ([all settings](../../../docs/configuration.md)) |
| `persistence.enabled` | `true` | Off: an `emptyDir`, data lost on every restart |
| `persistence.storageClass` / `size` / `accessModes` | `""` / `20Gi` / `[ReadWriteOnce]` | The data volume (empty class: the cluster default) |
| `persistence.existingClaim` | `""` | Use an existing claim instead of the StatefulSet's own |
| `service.s3` / `service.admin` | `ClusterIP`, 9000 / `ClusterIP`, 9001 | Services; keep the admin one `ClusterIP` |
| `ingress.s3` / `ingress.admin` | disabled | `className`, `annotations`, `host`, `tlsSecret` |
| `resources` | 100m / 128Mi request, 1Gi limit | Memory grows with concurrent uploads, not with stored data |
| `podSecurityContext` / `securityContext` | non-root UID 1654, read-only root file system, no capabilities | Pod and container security |
| `startupProbe` / `livenessProbe` / `readinessProbe` | `GET /health/live` | Probes |
| `nodeSelector`, `tolerations`, `affinity`, `priorityClassName`, `podAnnotations`, `podLabels` | empty | Scheduling and metadata |
| `tests.image` | `amazon/aws-cli:2.17.0` | Image of `helm test` |

## Notes

- **GitOps tools (Argo CD, TimeOps, Flux with `helm template`)** render without access to the cluster, so a generated
  root secret cannot be looked up and comes out different on every render. Set `rootCredentials.secretKey` (from your
  secret store) or `rootCredentials.existingSecret` there; then the Secret and the pod stay stable between syncs.

- **Data stays when you uninstall.** `helm uninstall` keeps the claim `data-<release>-timestore-0` (StatefulSet claims
  are never deleted by Helm), so a reinstall with the same release name finds the same buckets, objects and access
  keys. A generated root secret is replaced on reinstall; use `rootCredentials.existingSecret` to keep one root key
  pair for good. Delete the claim yourself to drop the data.
- **Backups:** see below; a `VolumeSnapshot` of the data claim (with a CSI driver that supports snapshots) works too.
- **Upgrades** replace the pod (stop, then start): S3 requests fail for the few seconds in between. Take a backup
  before you upgrade TimeStore itself.
- **Expected warning:** without TLS on the admin ingress, the server logs that the admin API listens on plain HTTP.
  It is reachable only inside the cluster (`ClusterIP`) unless you expose it.

## Back up

> **Beta:** the backup API, the `backup.*` and `restore.*` values and the archive format may still change before 1.0.
> A backup is a point-in-time copy, not a mirror ([docs/mirroring.md](../../../docs/mirroring.md)).

TimeStore backs itself up while it runs: one archive of the server at an instant, every blob checked
([docs/backup.md](../../../docs/backup.md)). The backup API takes requests signed like S3 requests (SigV4), not sign-in
cookies: create an access key with the **Backup** role in the admin UI and sign with it (`curl --aws-sigv4`, any AWS
SDK). Replace `ts` with your release and `storage` with the namespace.

```bash
kubectl -n storage port-forward svc/ts-timestore-admin 9001:9001 &   # 9001 taken here? 19001:9001 and :19001 below
ADMIN=http://127.0.0.1:9001
SIGN=(--aws-sigv4 "aws:amz:us-east-1:s3" --user "<backup key>:<secret>")   # region: config.region
```

**A backup in the data volume, then taken off it.** `POST /api/v1/backups` writes the archive to `/data/backups` on
the data volume (no second claim). That folder is where archives are **taken from**, not where they are kept: only
the newest `backup.keep` (default 1) stay, and a restore in place deletes the data claim with them. Download the
archive, keep it off the cluster, then delete it:

```bash
curl -s "${SIGN[@]}" -H 'Content-Type: application/json' -d '{}' "$ADMIN/api/v1/backups"      # id
curl -s "${SIGN[@]}" "$ADMIN/api/v1/backups/<id>"                                             # status succeeded
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/<id>/archive"                               # timestore-node-1-<time>.tar.zst
curl -s "${SIGN[@]}" -X DELETE "$ADMIN/api/v1/backups/<id>/archive"                          # frees the space (204)
```

While a backup runs, the data volume needs room for the data plus an archive as large (uncompressed, the worst case):
a backup that cannot fit is refused before it writes anything (`BACKUP_LOW_DISK_SPACE`). Tools that copy the whole
volume (restic, Kopia, Velero file backups) can skip `/data/backups`.

**Or stream it** straight to your machine (nothing written in the cluster):

```bash
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/stream"
```

## Restore a backup

A restore replaces the whole data directory with the archive's instant: objects written after the backup's instant
are not in the restored server. It only ever fills an **empty** data claim (an ext4 `lost+found` is fine), checks
every entry, and removes what it wrote if a check fails. The archive comes from **outside** the data volume (a restore
starts from an empty one): an `http(s)` URL, or a file from your machine.

### With `restore.archive` (values only)

Put the archive where the cluster can download it (a bucket of S3, R2 or B2 with a presigned URL, any web server) and
set the URL in the values; the pod restores it before the server starts (an init container running
`restore --if-empty`):

```yaml
restore:
  archive: https://…/timestore-node-1-20261119T050809Z.tar.zst?X-Amz-…   # presigned: a credential, not in Git
```

- An **empty** data claim is restored; a claim that **holds data is never touched**. Remove the value right after the
  check: left set, a claim that is lost later would be filled again with this old archive.
- A restore cut half way (the pod killed) starts over at the next start; the server never starts on a half-restored
  volume.
- Archives never hold the master key: the restored server uses `masterKey`, which must be the key the access keys
  were encrypted with (another key: the server refuses to start and says so).
- Watch it with `kubectl -n storage logs ts-timestore-0 -c restore`: **all checksums verified**, or why it refused (then
  the pod stays in `Init:Error`; fix the value and apply again).

**New cluster, or the data claim is gone:** install with `restore.archive` set; the new claim is empty and is filled
before the server starts.

**In place** (replace the data of a running release; the S3 API is down until the pod is back): stop the pod, delete
the data claim, apply the values with `restore.archive`. On TimeOps: **Scale → 0** on the StatefulSet, **Delete** the
claim on the cluster's resources page, add `restore.archive` to the Values, **Plan → Apply**.

```bash
kubectl -n storage scale statefulset ts-timestore --replicas=0
kubectl -n storage wait --for=delete pod/ts-timestore-0 --timeout=120s
kubectl -n storage delete pvc data-ts-timestore-0              # the data you replace (and /data/backups): no way back
helm upgrade ts oci://ghcr.io/tainguyen1501/charts/timestore -n storage --reuse-values \
  --set-string restore.archive='https://…/timestore-node-1-20261119T050809Z.tar.zst?X-Amz-…'
kubectl -n storage scale statefulset ts-timestore --replicas=1  # if the upgrade did not
kubectl -n storage logs ts-timestore-0 -c restore               # "all checksums verified"
```

**Side by side** (the old server keeps serving): install a second release (`ts2`) with `restore.archive` set and other
ingress hosts, check it, then move the ingress or DNS to it and uninstall the old release.

### An archive on your machine (manifests)

Without a URL: an empty data claim, a temporary pod, then copy and restore. The manifests are in
[examples/](examples/) (not part of the installed chart: a restore never starts by itself); each marks what to change
(`# CHANGE`).

| File | What |
|---|---|
| [data-claim.yaml](examples/data-claim.yaml) | The empty data claim, named `data-<release>-timestore-0` so the release uses it |
| [restore-pod.yaml](examples/restore-pod.yaml) | A pod to restore an archive from your machine (`kubectl cp`, then `exec`) |
| [restore-job.yaml](examples/restore-job.yaml) | A Job that restores an archive from a URL, without a release (`restore.archive` does the same with one) |

```bash
kubectl -n storage scale statefulset ts-timestore --replicas=0
kubectl -n storage wait --for=delete pod/ts-timestore-0 --timeout=120s
kubectl -n storage delete pvc data-ts-timestore-0              # the data you replace: no way back
kubectl -n storage apply -f examples/data-claim.yaml
kubectl -n storage apply -f examples/restore-pod.yaml
kubectl -n storage wait --for=condition=Ready pod/timestore-restore --timeout=120s
kubectl -n storage cp timestore-node-1-20261118T020000Z.tar.zst timestore-restore:/in/archive.tar.zst
kubectl -n storage exec timestore-restore -- /app/TimeStore.Server restore --archive /in/archive.tar.zst --data-path /data
kubectl -n storage delete pod timestore-restore
kubectl -n storage scale statefulset ts-timestore --replicas=1
```

For small and medium archives (the pod waits up to 6 hours): `kubectl cp` goes through the API server and starts over
when it breaks, and the copy needs as much free space on the node as the archive. A large one (many GB) goes better
from a URL. A refused restore (damaged archive, a claim that is not empty) leaves the claim as it was: fix the cause
and run the `restore` command again. After a lost cluster: the same without the two scale steps, then `helm install`
with the same release name.

**Master key:** archives never hold it; keep `masterKey` the same. An archive of an earlier version may hold one:
the restore writes it to `/data/master.key` and says so; put that key into `masterKey` (a Secret or `masterKey.value`).

**Upgrading from a chart version without a required master key:** the key is `/data/master.key` in the data volume.
Read it from the running pod **before** the upgrade (the new version does not start without the key), give it to the
chart, upgrade, then remove the old file so volume backups no longer carry it:

```bash
kubectl -n storage exec ts-timestore-0 -- base64 -w0 /data/master.key > timestore-master.key   # keep a copy
helm upgrade ts oci://ghcr.io/tainguyen1501/charts/timestore -n storage --reuse-values \
  --set masterKey.value="$(cat timestore-master.key)"
kubectl -n storage exec ts-timestore-0 -- rm /data/master.key
```
