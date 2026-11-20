# Configuration

TimeStore reads the `TimeStore` section of `appsettings.json` and environment variables with `__` as the
separator (`TimeStore__DataPath=/data`, `TimeStore__S3__RootSecretKey=...`). Environment variables win.

| Key | Default | Meaning |
|---|---|---|
| `NodeId` | `node-1` | This node's id, stored with every blob |
| `DataPath` | `data` | `metadata.db`, `blobs/`, `tmp/` |
| `S3Port` | `9000` | S3 API |
| `AdminPort` | `9001` | Admin UI and admin API |
| `AdminAddress` | `127.0.0.1` | Address of the admin port. On a network address without a TLS proxy, sign-in secrets travel in clear text (a warning is logged) |
| `Admin:SessionHours` | `24` | Lifetime of an admin sign-in (1–720) |
| `Admin:SecureCookies` | `false` | Mark the session cookie `Secure` on plain HTTP too: set it behind a TLS proxy |
| `Admin:OpenApi` | `false` (`true` in Development) | Publish the admin API's OpenAPI document at `/openapi/v1.json` (committed copy: `docs/api/openapi.v1.json`) |
| `Admin:UiPath` | — | Serve the admin UI from this directory instead of the copy embedded in the binary |
| `S3:Region` | `us-east-1` | Region clients sign with and `GetBucketLocation` reports |
| `S3:PublicUrl` | — | How clients outside reach the S3 port (`https://s3.example.com`, no path), for the URLs and share links the admin UI shows. Default: the admin page's host with `S3Port` |
| `S3:Domains` | `[]` | Domains for virtual-host addressing (`bucket.<domain>`); path style always works |
| `MasterKey` | — (required: this or `MasterKeyFile`) | The 32-byte key that encrypts stored access-key secrets (not objects), as Base64: `openssl rand -base64 32`. TimeStore never creates one; a key that does not decrypt the stored access keys is refused at startup. Keep a copy apart from the data: without it every access key must be created again ([backup](backup.md#the-master-key)) |
| `MasterKeyFile` | — | A file holding the master key instead (Base64, or the 32 raw bytes of a `master.key` of an earlier version): a mounted Secret, a Docker secret. Set one of the two, not both |
| `Lifecycle:Enabled` | `true` | Apply bucket lifecycle rules in the background |
| `Lifecycle:IntervalMinutes` | `60` | Minutes between lifecycle runs (1–1440); rules count whole days at midnight UTC |
| `Blobs:RetainDeletedDays` | `0` | Days a released blob is kept before it is deleted (0–3650), so an older metadata backup can still be restored. Set it to how long metadata backups are kept plus a margin (kept 30 days → `35`). Everything overwritten or deleted stays on disk this long: watch `retainedBytes` in the metrics ([blob retention](research/blob-retention.md)) |
| `Blobs:CollectSeconds` | `10` | How often released blobs whose wait is over are deleted (1–3600) |
| `Blobs:DeleteDelaySeconds` | `60` | How long a blob released by an overwrite, delete or abort stays readable (GETs in flight complete); 0 deletes at once |
| `BlobSweep:Enabled` | `true` | Remove orphan blobs (written but never committed) at startup and on an interval |
| `Multipart:AbandonAfterDays` | `7` | Abort multipart uploads nobody completed after this many days (0 = never); runs with the sweep |
| `BlobSweep:IntervalMinutes` | `360` | Time between maintenance runs: sweep and abandoned uploads (1–10080) |
| `BlobSweep:GracePeriodMinutes` | `60` | Blobs written more recently are never removed (minimum 5) |
| `S3:RootAccessKey` | — (required) | Root (break-glass) access key id: always valid, not stored, never listed or disabled through the API. Development: `timestore-dev` (`appsettings.Development.json`) |
| `S3:RootSecretKey` | — (required) | Root secret key, 8–128 characters. Development: `timestore-dev-secret`. Set it through environment variables (`TimeStore__S3__RootSecretKey`), never in a committed file |
| `S3:MaxClockSkewSeconds` | `900` | Allowed distance (1–3600 s) between a signed request's time and the server clock |
| `Backup:Path` | `<DataPath>/backups` | Where backups started from the admin API write their archives (`directory` target): a place to take archives from, not to keep them. A folder of its own: not `DataPath`, a folder holding it, nor its `blobs/` or `tmp/` ([backup](backup.md)) |
| `Backup:Keep` | `1` | Archives kept in that folder (1–100); older ones are deleted after a backup succeeds |
| `Backup:Compression` | `zstd` | Default compression of an archive: `zstd` (`.tar.zst`) or `none` (`.tar`) |
| `Backup:CompressionLevel` | `3` | Zstandard level 1–19 (higher: smaller, slower) |
| `Backup:MinFreeBytes` | `1073741824` | A backup refuses to start with less free space in `DataPath` (blobs released meanwhile stay until it ends); 0 turns the check off |
| `Replication:Enabled` | `true` | Run the replication worker and accept new rules. Off: buckets with a rule still queue their changes, sent once it is on again ([replication](user-guide/replication.md)) |
| `Replication:Concurrency` | `4` | Objects sent at the same time, over every rule (1–64) |
| `Replication:PollSeconds` | `5` | Queue read when no commit woke the worker: a safety net (1–86400) |
| `Replication:BatchDelayMilliseconds` | `200` | Wait after a wake-up, so a burst of writes to one key is sent once (0–10000) |
| `Replication:MaxBackoffSeconds` | `900` | Longest pause after repeated temporary destination failures (1, 2, 4 … seconds up to this) |
| `Replication:RequestTimeoutSeconds` | `900` | Longest time for one request to a destination: a whole object or one part (10–86400) |

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so the development key pair
applies. Any other environment refuses to start until the root key pair is configured.

## Behind a reverse proxy

Keep the admin port (`AdminPort`, 9001) on loopback, or put it behind a TLS proxy and set `Admin:SecureCookies=true`:
sign-in sends the secret key, and the session cookie must not travel in clear text. Set `S3:PublicUrl` to the address
clients use for the S3 port so the admin UI shows working URLs and share links.
