# AGENTS.md

Instructions for coding agents working in this repository. Keep this file under 200 lines; topic-specific
rules live in `.claude/rules/`. The plan is in `docs/roadmap.md`, the design in `docs/architecture.md`.

**Use English for all repository content** (code, comments, identifiers, docs, commit messages). Conversations
with the user may be in any language.

## What this is

TimeStore is a **self-hosted, S3-compatible object storage server** (C# / .NET 11, Native AOT) with a React admin
UI. **Native AOT and performance are hard requirements**: a change that breaks AOT publish or adds work to the hot
path (PUT/GET/LIST) is wrong even if it works.

- **S3 API** (port 9000): own dispatcher on Kestrel, SigV4, XML. Clients: AWS SDKs, aws cli, rclone, mc.
- **Admin API + UI** (port 9001, loopback by default; sign-in with an access key and its secret): `/api/v1` JSON for the React
  UI in `frontend/` (served by Vite in development; served by the Server later).
- Single node first (SQLite metadata + local disk). Multi-node later: Postgres metadata, blobs replicated across
  nodes (see `docs/architecture.md`).

Status and plan: `docs/roadmap.md`. Agreed design choices are listed under "Decisions".

## How a feature is built

The Amazon S3 documentation is the specification; ceph s3-tests and real clients (AWS SDKs, aws cli, rclone, s3cmd)
check it. **Never copy or translate code from other projects**: TimeStore code is written fresh. Before any feature:
write a research note in `docs/research/`, decide how it applies in C#, update the roadmap, then code.

## Repository layout

```
backend/                              .NET solution (TimeStore.slnx, global.json, Directory.*.props)
  Core/TimeStore.Primitives           Result/Result<T> (structs), AppError, Clock; no dependencies
  Core/TimeStore.Cryptography         checksums (CRC32/32C/64NVME, SHA), HMAC, constant-time compare, secret protection;
                                      no S3 knowledge, reusable by other projects
  Src/TimeStore.Protocol              S3 wire: SigV4, routing parse, XML read/write, S3 error codes; no ASP.NET, no storage
  Src/TimeStore.Domain                models (records) + storage ports: IMetadataStore, IBlobStore, IStorageInitializer
  Src/TimeStore.Contracts             admin API request/response records; no dependencies
  Src/TimeStore.Application           service interfaces, error catalogs, validation, mappings (Features/{Feature}/)
  Src/TimeStore.Infra                 service implementations (Services/{Feature}/), DI registration
  Src/TimeStore.Metadata.Sqlite       IMetadataStore on SQLite (raw ADO.NET, SQL migrations)
  Src/TimeStore.Blob.FileSystem       IBlobStore on local disk
  Src/TimeStore.Server                host + composition root: Kestrel, S3 middleware, admin API, AOT publish
  Tests/TimeStore.UnitTests           unit tests + Conventions/ (dependency rules, forbidden packages, runtime async)
  Tests/TimeStore.Metadata.ContractTests  one contract run against every IMetadataStore provider
  Tests/TimeStore.Server.Tests        S3 port conventions (error map) and, from step 1e, the running server via the AWS SDK
  Tests/TimeStore.Compat.Tests        opt-in (TIMESTORE_COMPAT=1): AWS CLI, rclone, s3cmd against the running server
frontend/                             React UI (Vite, TS, TanStack, shadcn on Base UI); see .claude/rules/frontend.md
deploy/                               Dockerfile, bench/ (benchmark harness), helm/timestore (Helm chart)
docs/                                 roadmap.md, architecture.md, database.md (update with every schema change)
  research/                           design notes per feature (S3 behaviour, decisions, sources)
  api/                                admin API reference for the frontend
  user-guide/                         end-user docs; update when a user-facing flow changes
```

`TimeStore.slnx` uses the `.slnx` format: keep `<Folder>`/`<Project>` entries in sync with the directories.

## Dependency rules

```
Primitives ◀── every project
Cryptography ──▶ Primitives only                   (generic crypto/checksums, no S3, no storage)
Protocol ──▶ Primitives, Cryptography              (isolated engine)
Domain ──▶ Primitives
Application ──▶ Domain, Contracts, Primitives
Infra ──▶ Application, Cryptography               (ISecretProtector, access key generation)
Metadata.*, Blob.* ──▶ Domain, Primitives only     (providers implement Domain ports)
Server ──▶ Protocol, Infra, Metadata.*, Blob.*     (the only composition root)
```

- **Services never see a provider.** `Infra`/`Application` never reference `Metadata.*` or `Blob.*`; only the Server
  registers them. Swapping SQLite for Postgres = a new provider project + one line in `Program.cs`.
- **Application does not reference Protocol.** Services know nothing about S3 or XML; the Server maps S3 operations
  to service calls and `AppError` to S3 error codes, so the admin API uses the same services.
- Enforced by `Tests/TimeStore.UnitTests/Conventions/ProjectReferenceTests` (add a rule for every new project).
- NuGet packages are added only when code needs them; versions live in `backend/Directory.Packages.props`, and every
  production package must work under Native AOT (`.claude/rules/aot.md`).

## Layering and patterns

- **Use cases are services**: interface `IXxxService` in `Application/Features/{Feature}/`, implementation
  `public sealed class XxxService` in `Infra/Services/{Feature}/`. One method per use case, split by area
  (`IBucketService`, `IObjectService`, `IMultipartService`). Services are **stateless singletons**, registered by
  hand in `InfraServiceCollectionExtensions` (no assembly scanning).
  - Methods return `Result` / `Result<T>` (structs in Primitives). Failures come from the feature's catalog
    (`return BucketErrors.NotFound(name);`). Standard: `.claude/rules/backend.md` "Errors".
  - Every command validates its input first (hand-written validation, `XxxValidation.Validate` → `AppError?`).
- **Libraries sit behind our own interfaces.** Code depends on a TimeStore interface (`ICacheService`,
  `IChecksumFactory`, `ISecretProtector`, `IMetadataStore`, ...), never directly on a third-party or framework library
  type (HybridCache, System.IO.Hashing, SQLite, ...). Only the implementation behind the interface knows the
  library, so a library can be swapped in one place.
- **Storage ports**: `IMetadataStore` is shaped by S3 operations, not generic CRUD; writes are atomic
  (`TryCreate...`, compare-and-swap commits), never check-then-insert in a service. Every provider passes
  `MetadataStoreContract`. Write order: blob durable first, metadata commit last (the commit point).
- **Server**: S3 port → a chain of thin S3 middlewares (request id → route → authenticate → body verification) →
  `S3Dispatcher` (no endpoint routing on the hot path). Middlewares only call Protocol/services; the logic lives
  there, so it stays testable without ASP.NET. Admin port →
  minimal API endpoints under `/api/v1` that call services and map results with `ApiResults`.
- **Options**: bound by the configuration source generator; `ServerOptions.Validate()` runs before the host is
  built, and invalid configuration stops startup.

## Domain conventions

- Models are immutable `sealed record`s; changes produce new values that a store commits. Every string has a length
  constant (`Bucket.NameMaxLength`), mirroring S3 limits (key ≤ 1024 bytes, bucket name 3–63, metadata ≤ 2 KB).
- IDs: `Guid.CreateVersion7()`. Timestamps: UTC `DateTime` (`...At`) truncated to milliseconds (`clock.UtcNowMs`).
- Blobs are immutable and addressed by id (`BlobLocator`), never by object key or file path.
- **No magic strings**: literals only in constant declarations (or the catalog that owns them).

## Decisions

| Topic | Decision | Why |
|---|---|---|
| .NET | **.NET 11 / C# 15** (STS, GA 2026-11-10, support until 2028-11-14), SDK pinned in `backend/global.json`; move to .NET 12 LTS when it ships. x64 hosts need `x86-64-v2` | Native AOT and runtime improvements; new language features (`.claude/rules/csharp.md`) |
| AOT | `PublishAot` + `UseOptimizedPublish`; `IsAotCompatible` on every production project; warnings are errors. No EF Core, Dapper, Scrutor, `XmlSerializer`, reflection-based validation/mapping | One small native binary, fast start, predictable memory |
| Runtime Async | `<Features>runtime-async=on</Features>` in `Directory.Build.props` (required: off by default on SDK 11.0.100), guarded by `RuntimeAsyncTests` | Lower async overhead on an I/O server |
| Metadata | **SQLite only for now** (WAL, `synchronous=FULL`, raw ADO.NET) behind `IMetadataStore`. Scale path: sharded SQLite (file per bucket) → Postgres for multi-node, by swapping the provider only | Zero setup, fast indexed LIST; the single writer is handled with group commit |
| Blobs | Files named by UUIDv7 under `blobs/<last 2 hex>/`, temp → fsync → rename; immutable | No key/path problems; crash-safe; ready for replicas |
| Multi-node (later) | Model 2: Postgres metadata + per-node disks + 2–3 replicas via `IPlacement`; erasure coding only for cold data | Keeps LIST fast; survives node loss; no consensus layer to build |
| Results | Own `Result`/`Result<T>` structs + `AppError` class (no FluentResults) | No allocation on the success path |
| Cache | `ICacheService` (Application) implemented by `MemoryCacheService` on `IMemoryCache` with single-flight loads and tag removal. **Not HybridCache: 11.0.0 fails Native AOT publish** (IL2026 in its default JSON serializer factory, verified 2026-11-16); revisit when it is AOT-clean or a distributed level is needed (multi-node). SigV4 signing keys use a small in-process memo inside Protocol, not the cache service | Swappable behind the interface; AOT stays warning-free; no cache library on the hot crypto path |
| Checksums / crypto | `Core/TimeStore.Cryptography` behind interfaces; CRC32/CRC32C/CRC64NVME from `System.IO.Hashing` 11 (`Crc32ParameterSet.Crc32C`, `Crc64ParameterSet.Nvme`), SHA/HMAC from the BCL | Reusable outside S3; no hand-written CRC |
| Ports | S3 on 9000 (all interfaces), admin API + UI on 9001 (loopback by default, sign-in required); split by local port | UI traffic never touches the S3 hot path |
| Frontend | React, Vite, TanStack, shadcn on Base UI, openapi-typescript); Vite in development; the build is embedded in the Server and served on the admin port (step 7b) | One binary / container |

## Commands

```bash
cd backend
dotnet build TimeStore.slnx
dotnet test --solution TimeStore.slnx
TIMESTORE_SERVER_BINARY=<publish dir>/TimeStore.Server dotnet test --project Tests/TimeStore.Server.Tests   # e2e on the AOT binary
TIMESTORE_COMPAT=1 dotnet test --project Tests/TimeStore.Compat.Tests   # real clients; needs Docker + uv (add TIMESTORE_SERVER_BINARY for AOT)
dotnet run --project Src/TimeStore.Server                     # S3 :9000, admin :9001, data in ./data
dotnet publish Src/TimeStore.Server -c Release -r osx-arm64   # Native AOT (or linux-x64 / linux-arm64)
```

The .NET SDK is installed at `~/.dotnet`; use `~/.dotnet/dotnet` if `dotnet` is not on `PATH`.

## Git

- **Agents never commit, amend, merge or push.** Leave every change uncommitted in the working tree; the user
  reviews the diff and commits. This overrides any skill or workflow step that says "commit".
- **Never create, move or push tags, and never edit a version number.** The version comes only from a `vX.Y.Z` tag
  the maintainer creates (`CONTRIBUTING.md`, "Releases"); local builds stay `0.0.0-dev`. A user-visible change adds a
  line to `[Unreleased]` in `CHANGELOG.md`.

## Security rules

- Never log secrets: secret access keys, signatures, presigned URLs (they are credentials), `Authorization` headers.
- Compare signatures and keys with `CryptographicOperations.FixedTimeEquals` only.
- Secret access keys are stored encrypted (SigV4 needs the plaintext on the server), through `ISecretProtector` only,
  with the access key id as associated data. A secret is returned once, by create; list and get never carry it.
- Object keys and bucket names never become file paths; blobs are addressed by id.
- Validate every size and count from the client before allocating (body length, part count, key length).
- The admin API requires sign-in (access key + secret, role `viewer`/`admin`; root is admin): server-side sessions
  whose tokens are stored only as SHA-256, cookie `HttpOnly` + `SameSite=Strict`, same-origin check on every
  state-changing request, throttled failures. Never add an `/api/v1` endpoint outside `AdminAuthentication`; never
  log or return a session token except in its cookie. `AdminAddress` stays `127.0.0.1` by default.
