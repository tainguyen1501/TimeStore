# S3 objects (phase 2): research and C# design

Status: **approved** (2026-11-16). AWS documentation is the specification. Covers: object metadata model, PutObject, GetObject/HeadObject, DeleteObject(s),
listing, bucket deletion, access keys in the database, orphan blobs. Multipart is phase 3, versioning phase 6.

---

## 1. Metadata model (SQLite)

### Decision

```sql
CREATE TABLE objects (
    bucket_id          BLOB    NOT NULL,                -- buckets.id
    key                TEXT    NOT NULL COLLATE BINARY, -- UTF-8 byte order = S3 listing order
    version_id         BLOB    NOT NULL,                -- UUIDv7; versioning (phase 6) adds history rows
    blob_id            BLOB    NOT NULL,                -- BlobLocator (id + node), never a path
    node_id            TEXT    NOT NULL,
    size               INTEGER NOT NULL,
    etag               TEXT    NOT NULL,                -- hex MD5 (multipart: md5-N, phase 3)
    content_type       TEXT    NOT NULL,
    checksum_algorithm TEXT,                            -- CRC32 | CRC32C | CRC64NVME | SHA1 | SHA256
    checksum           BLOB,
    headers            TEXT,                            -- stored system headers (Cache-Control, ...), compact text
    user_metadata      TEXT,                            -- x-amz-meta-*, compact text, ≤ 2 KB
    created_at         INTEGER NOT NULL,
    PRIMARY KEY (bucket_id, key)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_objects_blob ON objects (blob_id);      -- orphan-blob sweep
```

- `WITHOUT ROWID` + primary key `(bucket_id, key)`: rows are stored in listing order, so every listing is an index
  range scan and `GET` is one B-tree lookup.
- Headers and user metadata as a compact text encoding written/parsed by hand (no reflection serializer: AOT).
- `IMetadataStore` grows by S3-shaped methods: `FindObjectAsync`, `CommitObjectAsync` (upsert, returns the replaced
  blob), `DeleteObjectAsync` (returns the deleted blob), `ListObjectsAsync(bucket, prefix, delimiter, after, max)`,
  `DeleteBucketIfEmptyAsync`. Each one gets facts in `MetadataStoreContract`.

## 2. PutObject

### What AWS specifies

- Single PUT up to **5 GiB** (`EntityTooLarge`); Content-Length (or `x-amz-decoded-content-length`) required
  (`MissingContentLength`, 411).
- `Content-MD5` (base64 of 16 bytes): malformed → `InvalidDigest`, mismatch → `BadDigest`.
- `x-amz-meta-*` user metadata: total of names + values ≤ **2 KB** (`MetadataTooLarge`); names case-insensitive,
  returned lower-cased.
- Stored and returned headers: `Content-Type` (default `binary/octet-stream`), `Cache-Control`, `Content-Disposition`,
  `Content-Encoding` (minus `aws-chunked`), `Content-Language`, `Expires`.
- Response: `ETag` (quoted hex MD5 for single PUT), checksum header when one was sent.
- Overwrite is last-writer-wins; no partial object is ever visible.

### Decision

1. Validate everything that does not need the body first: key (incl. section 7), sizes, metadata size, header syntax,
   `Content-MD5` format, bucket exists.
2. Stream `Request.Body` (already verifying: SigV4/chunked/checksums, step 1c) into `IBlobStore.WriteAsync`, which
   computes MD5 on the way. **fsync the shard directory after the rename** (carry-over; needs a small P/Invoke on
   Unix, `LibraryImport`, AOT-safe).
3. Compare `Content-MD5`; on any failure delete the new blob.
4. `CommitObjectAsync` in one transaction (the commit point), then delete the replaced blob (best effort; the sweep
   in section 9 catches failures).
5. Respond `200`, `ETag`, `x-amz-checksum-*` when the request carried one.

No per-object lock: the store's upsert decides the race (last commit wins), as planned for multi-node.
`If-None-Match: *` (conditional create) lands with conditional requests (section 3).

## 3. GetObject / HeadObject

### What AWS specifies

- Headers: `Content-Length`, `Content-Type`, `ETag`, `Last-Modified` (RFC 1123), `Accept-Ranges: bytes`, stored
  system headers, `x-amz-meta-*`; checksums only with `x-amz-checksum-mode: ENABLED`.
- `Range: bytes=a-b | a- | -n` → `206` + `Content-Range`; unsatisfiable → `416 InvalidRange`. Multiple ranges are not
  supported by S3 (the whole object is returned).
- Conditional: `If-Match` / `If-Unmodified-Since` → `412 PreconditionFailed`; `If-None-Match` /
  `If-Modified-Since` → `304 Not Modified`.
- Response overrides via query (presigned URLs): `response-content-type`, `response-content-disposition`,
  `response-cache-control`, `response-content-encoding`, `response-content-language`, `response-expires`.

### Decision

- Implement all of the above in phase 2 (conditional requests move here from phase 6: small, and caches/browsers
  rely on them).
- ~~Zero-copy via `SendFileAsync`~~ **Corrected during 2c:** Kestrel has no kernel `sendfile`; its `SendFileAsync`
  opens the file and copies through a stream (only HTTP.sys sends files from the kernel). So the service **opens the
  blob first** (`IObjectService.OpenAsync`, seekable stream) and the handler copies the range through one pooled
  128 KiB buffer. Holding the handle also keeps a read alive when a concurrent overwrite deletes the old blob (Unix
  semantics); if the blob vanishes between lookup and open, the service looks up once more. Real zero-copy (e.g.
  `io_uring`/`sendfile` via a custom transport) stays a phase 5 measurement topic.
- `HEAD`: same headers, no body.

## 4. DeleteObject / DeleteObjects

- `DELETE /bucket/key` → `204` even when the key does not exist (idempotent).
- `POST /bucket?delete`: XML body, ≤ **1000** keys (`MalformedXML` above), `Content-MD5` or an `x-amz-checksum-*`
  header **required**; `<Quiet>` returns only errors; per-key `<Deleted>`/`<Error>`. Read with `S3XmlReader`
  (larger limit, e.g. 1 MiB).
- Blobs are deleted after the metadata delete commits.

## 5. Listing (ListObjects v1 / v2)

### The delimiter problem

Grouping common prefixes while scanning rows is the obvious approach and the slow one: one `logs/` common prefix with
1 000 000 objects under it costs 1 000 000 rows read to emit one entry. Walking directories makes a common prefix
cheap, but ties the metadata to a filesystem shape.

### Decision: skip-scan on the primary key

```
cursor = max(startAfter/token, prefix)
loop until max entries:
    row = first row WHERE bucket_id = ? AND key > cursor AND key >= prefix ORDER BY key LIMIT batch
    if row has delimiter after prefix → emit common prefix P; cursor = P + U+10FFFF (jump past every key under P)
    else emit object; cursor = key
```

Every emitted entry costs one index seek (batched for runs of plain objects), whatever the number of keys under a
common prefix. **Measured (2026-11-16):** a delimiter listing takes 0.04 ms with 2 000 and with 100 000 keys under
`logs/`. Keys whose next character is U+10FFFF are caught by an equality check on the last emitted prefix.

### Continuation tokens

Opaque by S3's definition: **always base64url of the last emitted key or prefix**. Keeping bare keys and encoding only
XML-unsafe ones creates an ambiguity that has to be resolved heuristically; an always-encoded token has
none. `StartAfter`/`Marker` are plain keys as S3 defines them.

## 6. Bucket deletion

`DeleteBucket` succeeds only when the bucket has no objects: `BucketNotEmpty` (409). Atomic in the store
(`DELETE ... WHERE NOT EXISTS (SELECT 1 FROM objects ...)`) so a concurrent PUT cannot slip in between a check and the
delete. A contract fact covers it.

## 7. Keys that XML 1.0 cannot carry

S3 accepts any UTF-8 key, including control characters (U+0000–U+001F except tab, LF, CR). XML 1.0 cannot represent
them: our `XmlWriter` would throw and fail the whole listing.

**Decision:** reject such keys on write (`PutObject`, `CopyObject`, `CreateMultipartUpload`) with `InvalidArgument`
("Object key contains characters that cannot be listed"). A deliberate, documented difference from AWS; these keys
are almost always bugs and cannot be listed without `encoding-type=url` anyway. Alternative: accept them and require
`encoding-type=url` to list them (error otherwise).

## 8. Access keys in the database

- Table `access_keys (access_key_id PK, secret_ciphertext BLOB, description, created_at, disabled_at)`; the secret is
  needed in plain text for SigV4, so it is **encrypted at rest** with AES-256-GCM via `ISecretProtector` in
  `TimeStore.Cryptography` (BCL `AesGcm`, AOT-safe). Master key: a 32-byte key file in the data directory created on
  first start with owner-only permissions, overridable by `TimeStore:MasterKeyFile`/environment.
- `ICredentialStore` implementation: database keys + the configured root key (break-glass), cached through
  `ICacheService` and invalidated on change (misses are not cached, so random ids cannot fill the cache).
  `SigningKeyCache` stays as it is: it is reached only with a secret the credential store returned, so a disabled or
  deleted key never gets there, and an entry checks its secret before use.
- Admin API: create (secret returned once), list, disable, delete; UI page in phase 7.

## 9. Orphan blobs

A crash between blob rename and metadata commit (or a failed best-effort delete) leaves blobs nothing references.
A background sweep (single node: in-process lease) lists blob files older than a grace period (e.g. 1 hour) and
deletes those with no `objects` row (`ix_objects_blob`). Runs at startup and every N hours.

Two safety rules: a **cutoff time** so blobs whose metadata commit may still be in flight are never candidates, and
**refusing to purge when the metadata is incomplete** (unresolvable manifests). TimeStore is a single node with the
metadata in one SQLite file, so the set difference is cheap and can run in-process.

**Design (step 2g).**
- `IBlobStore.ListAsync(writtenBefore)` streams this node's blob files (`BlobFile(BlobId, Size, WrittenAt)`) whose
  last write is older than the cutoff. Only names that are a blob id in their correct shard are listed: unknown
  files are never touched. `DeleteAsync` takes a blob id (the store is always this node's).
- `IMetadataStore.FindUnreferencedBlobsAsync(blobIds)` returns the ids no row references, in batches of 500 through
  `ix_objects_blob`. It is the **one place** that knows which tables hold blob references: multipart parts
  (phase 3) and versions (phase 6) must be added to it.
- Grace period on the file's last-write time, not on the UUIDv7 timestamp: the id is taken when a write starts, so a
  slow 5 GiB upload has an old id but a fresh file. The last write happens just before fsync → rename → commit, so
  1 h (default, minimum 5 min) leaves ample margin.
- `IBlobSweepService.SweepAsync` (Infra) runs one sweep under an in-process lock (a second caller gets
  `MAINTENANCE_SWEEP_RUNNING`) and returns counts only. A hosted worker in the Server runs it at startup and every
  `TimeStore:BlobSweep:IntervalMinutes` (default 360); `POST /api/v1/maintenance/blob-sweep` runs it on demand.
- Multi-node later: each node sweeps its own blobs against the shared metadata; the lease then moves to the DB.

## 10. Proposed phase 2 steps

| Step | Content | Proof |
|---|---|---|
| 2a ✅ | Schema migration v2 (`objects`), store methods + contract facts (upsert race, delete-returns-blob, bucket-not-empty, listing with skip-scan) | Contract tests on SQLite |
| 2b ✅ | PutObject: validation, metadata/headers, `Content-MD5`, checksum storage, directory fsync, commit + replaced-blob delete | Unit + e2e via AWS SDK (all checksum algorithms, 0 B, 5 MiB, overwrite) |
| 2c ✅ | GetObject/HeadObject: headers, ranges, conditional requests, response overrides, `SendFileAsync` | e2e: ranges, 304/412, presigned GET with overrides |
| 2d ✅ | DeleteObject, DeleteObjects (XML, quiet, Content-MD5), DeleteBucket only when empty | e2e |
| 2e ✅ | ListObjects v1/v2 for real: skip-scan, tokens, url encoding | Contract tests with 10 000 keys under one prefix (seek count), e2e paging |
| 2f ✅ | Access keys in the database: `ISecretProtector`, store, admin API, cache | Unit + e2e (create key, sign with it, disable, rejected) |
| 2g ✅ | Orphan-blob sweep: grace period on last write, DB is the authority, worker + admin trigger | Unit (crash simulation, fresh/unknown files kept) + e2e |

## 11. Decisions (2026-11-16)

1. Keys with XML-invalid characters are **rejected on write** (`InvalidArgument`).
2. Conditional requests (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`, and
   `If-None-Match: *` on PUT) are **part of phase 2** (step 2c / 2b).
3. Access keys in the database are **step 2f of phase 2**.
4. Master key: a 32-byte file created on first start in the data directory (`master.key`, owner-only), overridable
   with `TimeStore:MasterKeyFile` (e.g. a Kubernetes secret mount).
5. The configured root key stays as a **break-glass key**: always valid, never listed, disabled or deleted through
   the API.
