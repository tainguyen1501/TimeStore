# TimeStore Helm chart

Runs [TimeStore](https://github.com/tainguyen1501/TimeStore), a self-hosted S3-compatible object storage server, as
one pod with a persistent volume. TimeStore is single node today: the chart always runs one replica, and a
StatefulSet makes sure two pods never write the same volume.

## Install

```bash
helm install timestore oci://ghcr.io/tainguyen1501/charts/timestore --version 0.1.0 \
  --namespace storage --create-namespace
```

Requires Kubernetes 1.25+ and Helm 3.8+ (OCI charts). After the install, Helm prints how to read the generated root
key pair and how to reach the S3 API and the admin UI. Check the installation with `helm test timestore -n storage`:
it creates a bucket, writes, reads and deletes an object through the S3 service.

From a clone of the repository: `helm install timestore ./deploy/helm/timestore -n storage --create-namespace`.

## Typical values

```yaml
# values-prod.yaml
rootCredentials:
  existingSecret: timestore-root        # keys: accessKey, secretKey
masterKey:
  existingSecret: timestore-master-key  # key: master.key (32 bytes)
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
head -c 32 /dev/urandom > master.key
kubectl -n storage create secret generic timestore-master-key --from-file=master.key
# keep a copy of master.key outside the cluster: see docs/backup.md
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
| `masterKey.existingSecret` / `key` | `""` / `master.key` | Secret with the 32-byte master key; without it the key is created inside the data volume |
| `config.region` | `us-east-1` | `S3:Region` |
| `config.publicUrl` | `""` | `S3:PublicUrl`; empty: from `ingress.s3` |
| `config.domains` | `[]` | `S3:Domains`, for virtual-host addressing |
| `config.secureCookies` | `null` | `Admin:SecureCookies`; null: true when `ingress.admin` has TLS |
| `config.sessionHours` | `24` | `Admin:SessionHours` |
| `backup.existingClaim` | `""` | A claim you create for backups started from the admin API; archives go to its folder `<release>/` (`Backup:Path=/backups/<release>`); empty: streamed backups only |
| `restore.archive` | `""` | Restore this archive into the data volume when it is empty, before the server starts (an init container); a file on the backups claim or an http(s) URL. See "Restore a backup" |
| `backup.compression` / `compressionLevel` / `minFreeBytes` | `zstd` / `3` / `1073741824` | `Backup:Compression`, `Backup:CompressionLevel` (1–19), `Backup:MinFreeBytes` |
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

**Stream** an archive to your machine (nothing is kept in the cluster):

```bash
kubectl -n storage port-forward svc/ts-timestore-admin 9001:9001 &   # 9001 taken here? 19001:9001 and :19001 below
ADMIN=http://127.0.0.1:9001
SIGN=(--aws-sigv4 "aws:amz:us-east-1:s3" --user "<backup key>:<secret>")   # region: config.region
curl -fsS "${SIGN[@]}" -OJ "$ADMIN/api/v1/backups/stream"   # timestore-node-1-<time>.tar.zst
```

**Keep archives in the cluster:** create a claim for them yourself
([examples/backups-claim.yaml](examples/backups-claim.yaml): it then outlives the release and the data volume; any
storage class works, archives are plain files) and point `backup.existingClaim` at it. Archives go to the folder
`<release>/` on it, so several releases (or a restored second release) can share one claim:

```bash
kubectl -n storage apply -f examples/backups-claim.yaml
helm upgrade ts oci://ghcr.io/tainguyen1501/charts/timestore -n storage --reuse-values \
  --set backup.existingClaim=timestore-backups
curl -s "${SIGN[@]}" -H 'Content-Type: application/json' -d '{}' "$ADMIN/api/v1/backups"
curl -s "${SIGN[@]}" "$ADMIN/api/v1/backups"                 # status, archiveName (in the claim's folder ts/)
```

With `ReadWriteOnce` claims on node-local storage (k3s `local-path`), both claims bind to the pod's node.

## Restore a backup

A restore replaces the whole data directory with the archive's instant: objects written after the backup's instant
are not in the restored server. It only ever fills an **empty** data claim (an ext4 `lost+found` is fine), checks
every entry, and removes what it wrote if a check fails.

### With `restore.archive` (values only)

Set the archive in the values; the pod restores it before the server starts (an init container running
`restore --if-empty`):

```yaml
restore:
  archive: /backups/ts/timestore-node-1-20261119T050809Z.tar.zst   # on the backups claim (backup.existingClaim), or
  # archive: https://…/timestore-node-1-….tar.zst?X-Amz-…           # an http(s) URL, e.g. presigned (a credential: not in Git)
```

- An **empty** data claim is restored; a claim that **holds data is never touched**, so the value is safe to leave set
  across restarts and upgrades. Remove it once the server runs on the restored data.
- A restore cut half way (the pod killed) starts over at the next start; the server never starts on a half-restored
  volume.
- The master key goes to `/data/master.key` when the archive holds it (an admin key asked for it). With
  `masterKey.existingSecret` the server keeps using the Secret's key, and the archive does not need one.
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
kubectl -n storage delete pvc data-ts-timestore-0              # the data you replace: check the archive first
helm upgrade ts oci://ghcr.io/tainguyen1501/charts/timestore -n storage --reuse-values \
  --set restore.archive=/backups/ts/timestore-node-1-20261119T050809Z.tar.zst
kubectl -n storage scale statefulset ts-timestore --replicas=1  # if the upgrade did not
kubectl -n storage logs ts-timestore-0 -c restore               # "all checksums verified"
```

**Side by side** (the old server keeps serving): install a second release (`ts2`) with `restore.archive` set and other
ingress hosts, check it, then move the ingress or DNS to it and uninstall the old release.

### Without `restore.archive` (manifests)

For a restore by hand, or into a claim no release uses yet. The manifests are in [examples/](examples/) (not part of
the installed chart: a restore never starts by itself). Each marks what to change (`# CHANGE`): the image version, the
archive (`/backups/<release>/<archiveName>`), the release in the claim's name.

| File | What |
|---|---|
| [data-claim.yaml](examples/data-claim.yaml) | The empty data claim, named `data-<release>-timestore-0` so the release uses it |
| [restore-job.yaml](examples/restore-job.yaml) | A Job that restores an archive from the backups claim |
| [restore-pod.yaml](examples/restore-pod.yaml) | A pod to restore an archive from your machine (`kubectl cp`, then `exec`) |
| [backups-claim.yaml](examples/backups-claim.yaml) | The backups claim (`backup.existingClaim`) |

**In place with a Job** (the S3 API is down until the pod is back):

```bash
kubectl -n storage scale statefulset ts-timestore --replicas=0
kubectl -n storage wait --for=delete pod/ts-timestore-0 --timeout=120s
kubectl -n storage delete pvc data-ts-timestore-0              # the data you replace: check the archive first
kubectl -n storage apply -f examples/data-claim.yaml
kubectl -n storage apply -f examples/restore-job.yaml
until kubectl -n storage get job timestore-restore -o jsonpath='{.status.conditions[*].type}' | grep -qwE 'Complete|Failed'; do sleep 5; done
kubectl -n storage logs job/timestore-restore                  # "all checksums verified", or why it refused
kubectl -n storage get job timestore-restore                   # STATUS Complete: restored; Failed: refused
kubectl -n storage delete job timestore-restore
kubectl -n storage scale statefulset ts-timestore --replicas=1
```

A refused restore (wrong archive name, damaged archive, a claim that is not empty) leaves the claim as it was: fix the
cause, `kubectl delete job timestore-restore`, apply the Job again.

After a lost cluster or a `helm uninstall` with its claim deleted: the same without the two scale steps, then
`helm install` with the same release name.

**Side by side with a Job** (the old server keeps serving until you switch): restore into the claim of a second release
(`data-ts2-timestore-0` in both files), install it, check it, then move the ingress or DNS to it and uninstall the
old release. Both releases may use the same backups claim: `ts2` writes to its own folder `ts2/` on it.

```bash
kubectl -n storage apply -f examples/data-claim.yaml           # name: data-ts2-timestore-0, instance: ts2
kubectl -n storage apply -f examples/restore-job.yaml          # claimName: data-ts2-timestore-0
until kubectl -n storage get job timestore-restore -o jsonpath='{.status.conditions[*].type}' | grep -qwE 'Complete|Failed'; do sleep 5; done
kubectl -n storage logs job/timestore-restore                  # "all checksums verified", or why it refused
kubectl -n storage get job timestore-restore                   # STATUS Complete: restored; Failed: refused
kubectl -n storage delete job timestore-restore
helm install ts2 oci://ghcr.io/tainguyen1501/charts/timestore -n storage -f values.yaml
```

**An archive on your machine** (a streamed backup): instead of the Job, the pod, then copy and restore. For small and
medium archives; the pod waits up to 6 hours. A large one (many GB) goes better from the backups claim with the Job:
`kubectl cp` goes through the API server and starts over when it breaks, and the copy needs as much free space on the
node as the archive.

```bash
kubectl -n storage apply -f examples/restore-pod.yaml
kubectl -n storage wait --for=condition=Ready pod/timestore-restore --timeout=120s
kubectl -n storage cp timestore-node-1-20261118T020000Z.tar.zst timestore-restore:/in/archive.tar.zst
kubectl -n storage exec timestore-restore -- /app/TimeStore.Server restore --archive /in/archive.tar.zst --data-path /data
kubectl -n storage delete pod timestore-restore
```

**Master key:** an archive holds it only when the backup asked for it (`includeMasterKey=true`). With
`masterKey.existingSecret`, keep that Secret and restore an archive without the key. With neither, the server creates
a new key and the access keys stored before stop working (the root key pair still works).
