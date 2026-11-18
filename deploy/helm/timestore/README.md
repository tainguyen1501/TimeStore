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
- **Backups:** take a `VolumeSnapshot` of the claim (with a CSI driver that supports snapshots) or follow
  [docs/backup.md](../../../docs/backup.md).
- **Upgrades** replace the pod (stop, then start): S3 requests fail for the few seconds in between. Take a backup
  before you upgrade TimeStore itself.
- **Expected warning:** without TLS on the admin ingress, the server logs that the admin API listens on plain HTTP.
  It is reachable only inside the cluster (`ClusterIP`) unless you expose it.
