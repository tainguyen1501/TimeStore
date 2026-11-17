# TimeStore architecture

## Layers

```
                 ┌──────────── TimeStore.Server (composition root, Native AOT) ────────────┐
 S3 :9000  ──▶   │ S3Pipeline (ids → route → auth → body) → S3Dispatcher ─┐                 │
 Admin :9001 ──▶ │ /api/v1 endpoints (ApiResults) ─────────────┼──▶ services (Infra)         │
                 └─────────────────────────────────────────────┼──────────────────────────┘
                     TimeStore.Protocol (SigV4, XML, errors)   │
                                                               ▼
                                         ports (Domain): IMetadataStore, IBlobStore
                                                               │
                                ┌──────────────────────────────┴─────────────────┐
                                ▼                                                ▼
                     Metadata.Sqlite (now) / Metadata.Postgres (later)   Blob.FileSystem (now) / remote blobs (later)
```

- **Services** hold the S3 semantics (validation, versioning rules, ETags) and call ports. They never see SQL, file
  paths or HTTP.
- **Ports** are shaped by S3 operations, so each backend can implement them its own way (a SQL index range scan, a
  directory walk, a distributed table). `MetadataStoreContract` pins the behaviour.
- **Providers** are separate projects; only the Server references them. Changing the database is a provider change.

## Storage model (single node)

```
data/
  metadata.db              SQLite (WAL): buckets, objects, versions, uploads, parts, access keys
  master.key               32-byte AES key for stored secrets (owner-only; TimeStore:MasterKeyFile moves it)
  blobs/<xy>/<uuidv7>      immutable blob files; <xy> = last two hex chars of the id
  tmp/<uuidv7>.tmp         in-flight writes; deleted at startup
```

Object keys never become paths (no length, character or case-sensitivity problems).

## Write path (PutObject)

1. Validate request (sizes, key, headers) before reading the body.
2. Stream the body into `IBlobStore.WriteAsync`: pooled buffer, MD5 (and checksum) computed on the way, temp file,
   `fsync`, atomic rename → `BlobLocator`.
3. `IMetadataStore.CommitObjectVersion(...)` in one transaction (the commit point); it returns the previous blob, if
   any. Before this step the blob's directory entry is durable too: the shard directory is fsynced after the rename
   (`DirectorySync`, libc via `LibraryImport`; no-op on Windows).
4. Delete the previous blob asynchronously (unversioned overwrite).

A crash after 2 and before 3 leaves an orphan blob: the orphan-blob sweep (`BlobSweepService`, at startup and every
6 h) removes blob files older than the grace period (1 h, measured on the last write) that no metadata row
references. A crash never leaves metadata pointing at a missing blob.

## Read path (GetObject)

`FindObject` (index lookup) → open the blob (the handle survives a concurrent overwrite) → preconditions (304/412) →
range (206/416) → copy through one pooled buffer, headers from metadata. Kestrel has no kernel `sendfile`, so
`SendFileAsync` would be the same copy.

## Concurrency

- SQLite has one writer. Every metadata write goes through a single writer task fed by a `Channel`, committing
  batches in one transaction (group commit, a `SAVEPOINT` per write), so many PUTs share one `fsync` of the WAL.
- Atomic store methods (`TryCreateBucket`, compare-and-swap commits) decide races, not in-process locks; this is what
  keeps the model valid with several nodes later.

## Multi-node (planned, model 2)

- Metadata in Postgres (provider swap); every node serves S3 and is stateless.
- Blobs on each node's disks, 2–3 replicas chosen by `IPlacement` (zones + capacity); the
  locator lists every replica.
- Erasure coding only for cold data later.

## Ports

| Port | Purpose |
|---|---|
| 9000 | S3 API (and `/health/live`) |
| 9001 | Admin API `/api/v1`, `/health/live`; React UI later (Vite in development). Loopback by default (`AdminAddress`) until authentication |
