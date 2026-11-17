# TimeStore

Self-hosted, S3-compatible object storage written in C# (.NET 11, Native AOT), with a React admin UI.

- **S3 API** on port `9000`: works with AWS SDKs, `aws` CLI, rclone and `mc` (as operations land; see
  `docs/roadmap.md`).
- **Admin API** on port `9001`, bound to `127.0.0.1` by default. Sign in with an access key and its secret: the root key pair is always an admin, other keys need the `viewer` or `admin` role. The React admin UI is served on
  the same port, embedded in the binary (`docs/user-guide/sign-in.md`); in development it runs with Vite
  (`frontend/`, `pnpm dev`). Build `frontend/` (`pnpm build`) before the Server to embed it; `deploy/Dockerfile`
  does both.
- Single node today (SQLite metadata, blobs on local disk); multi-node replication is planned.

## Run

```bash
cd backend
dotnet run --project Src/TimeStore.Server
curl localhost:9001/health/live
```

Or as a Linux container (Native AOT):

```bash
docker build -f deploy/Dockerfile -t timestore .
docker run -p 9000:9000 -p 127.0.0.1:9001:9001 -v timestore-data:/data \
  -e TimeStore__S3__RootAccessKey=<key> -e TimeStore__S3__RootSecretKey=<secret> timestore
```

S3 clients use the root key pair and region `us-east-1`, path-style addressing. Supported today: buckets
(create, list, head, delete, location, versioning, lifecycle rules, bucket policies) and objects (put, get with
ranges and conditional requests, head, delete, multi-delete, copy, listings v1/v2 and object versions); multipart
uploads (create, upload part, upload part copy, complete with composite and full-object checksums, abort, both
listings, GET/HEAD with ranges and `partNumber`, abandoned uploads aborted after `Multipart:AbandonAfterDays`).
Unsigned (anonymous) requests are refused unless a bucket policy allows them; access keys can be limited to what
bucket policies grant (`docs/research/s3-bucket-policy.md`). Requests for
features that do not exist yet (server-side encryption, object lock, tags, storage classes other than `STANDARD`) are
refused with `NotImplemented`, never silently ignored. ACLs behave as on Amazon S3 buckets with ACLs disabled (the
default for new buckets): objects accept only `bucket-owner-full-control`, anything else is
`AccessControlListNotSupported`. See
`docs/roadmap.md` for what is next. Example with the AWS CLI:

```bash
export AWS_ACCESS_KEY_ID=timestore-dev AWS_SECRET_ACCESS_KEY=timestore-dev-secret AWS_DEFAULT_REGION=us-east-1
aws --endpoint-url http://localhost:9000 s3 mb s3://photos
aws --endpoint-url http://localhost:9000 s3 ls
```

Configuration (`appsettings.json`, section `TimeStore`, or environment variables such as `TimeStore__DataPath`):

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
| `MasterKeyFile` | `<DataPath>/master.key` | 32-byte key that encrypts stored access-key secrets; created on first start (owner-only). Back it up apart from the data: without it, stored keys are unusable |
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

## Docs

- `docs/architecture.md`: layers, storage model, write path, multi-node plan
- `docs/roadmap.md`: phases and status
- `docs/performance.md`: how to benchmark, and the baselines
- `docs/research/`: design notes per feature, with sources
- `CONTRIBUTING.md`: how to build, test and send a pull request (contributors sign `CLA.md`)
- `SECURITY.md`: how to report a vulnerability privately
- `CHANGELOG.md`: changes per version; releases and the tag format are in `CONTRIBUTING.md`
- `AGENTS.md`: conventions for contributors and coding agents

## License

TimeStore is licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).

- Running unmodified TimeStore is free for any purpose, including commercial use. Applications that talk to it over
  the S3 API are not affected by its license.
- If you modify TimeStore and people use your modified version over a network (for example a hosted service), you
  must offer those users its source code under the AGPL-3.0.
- A commercial license without these terms is available from the maintainers on request.

Copyright (C) 2026 tainguyen1501 and contributors.
