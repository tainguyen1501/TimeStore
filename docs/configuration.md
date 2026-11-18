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
| `MasterKeyFile` | `<DataPath>/master.key` | 32-byte key that encrypts stored access-key secrets; created on first start (owner-only). Back it up apart from the data: without it, stored keys are unusable ([backup](backup.md)) |
| `Lifecycle:Enabled` | `true` | Apply bucket lifecycle rules in the background |
| `Lifecycle:IntervalMinutes` | `60` | Minutes between lifecycle runs (1–1440); rules count whole days at midnight UTC |
| `Blobs:DeleteDelaySeconds` | `60` | How long a blob released by an overwrite, delete or abort stays readable (GETs in flight complete); 0 deletes at once |
| `BlobSweep:Enabled` | `true` | Remove orphan blobs (written but never committed) at startup and on an interval |
| `Multipart:AbandonAfterDays` | `7` | Abort multipart uploads nobody completed after this many days (0 = never); runs with the sweep |
| `BlobSweep:IntervalMinutes` | `360` | Time between maintenance runs: sweep and abandoned uploads (1–10080) |
| `BlobSweep:GracePeriodMinutes` | `60` | Blobs written more recently are never removed (minimum 5) |
| `S3:RootAccessKey` | — (required) | Root (break-glass) access key id: always valid, not stored, never listed or disabled through the API. Development: `timestore-dev` (`appsettings.Development.json`) |
| `S3:RootSecretKey` | — (required) | Root secret key, 8–128 characters. Development: `timestore-dev-secret`. Set it through environment variables (`TimeStore__S3__RootSecretKey`), never in a committed file |
| `S3:MaxClockSkewSeconds` | `900` | Allowed distance (1–3600 s) between a signed request's time and the server clock |

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so the development key pair
applies. Any other environment refuses to start until the root key pair is configured.

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so the development key pair
applies. Any other environment refuses to start until the root key pair is configured.

## Behind a reverse proxy

Keep the admin port (`AdminPort`, 9001) on loopback, or put it behind a TLS proxy and set `Admin:SecureCookies=true`:
sign-in sends the secret key, and the session cookie must not travel in clear text. Set `S3:PublicUrl` to the address
clients use for the S3 port so the admin UI shows working URLs and share links.
