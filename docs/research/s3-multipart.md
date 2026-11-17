# Research: multipart upload (phase 3)

Sources, in order: the Amazon S3 API Reference (CreateMultipartUpload, UploadPart, CompleteMultipartUpload,
AbortMultipartUpload, ListParts, ListMultipartUploads, GetObject `partNumber`, "Checking object integrity"),
and ceph s3-tests.

## 1. What AWS specifies

| Operation | Request | Notes |
|---|---|---|
| CreateMultipartUpload | `POST /{key}?uploads` | Takes the object's headers now (`Content-Type`, `x-amz-meta-*`, `Cache-Control`, ...), plus `x-amz-checksum-algorithm` and `x-amz-checksum-type` (`COMPOSITE` \| `FULL_OBJECT`). Returns `UploadId` |
| UploadPart | `PUT /{key}?partNumber=N&uploadId=U` | N in 1–10 000. Part ≤ 5 GiB. `Content-MD5` and `x-amz-checksum-*` as for PutObject; the part's checksum must use the upload's algorithm. Re-uploading a number replaces the part. Returns `ETag` = MD5 of the part |
| CompleteMultipartUpload | `POST /{key}?uploadId=U` | XML list of `Part { PartNumber, ETag, Checksum* }`. Errors: `MalformedXML` (empty list), `InvalidPartOrder` (not strictly ascending), `InvalidPart` (missing part or ETag mismatch), `EntityTooSmall` (any part but the last under 5 MiB), `NoSuchUpload`. Parts uploaded but not listed are discarded. Supports `If-None-Match: *` and `If-Match`. May answer 200 with an error document (long completions); a retry of a completion that succeeded returns success |
| AbortMultipartUpload | `DELETE /{key}?uploadId=U` | 204; `NoSuchUpload` for an unknown id |
| ListParts | `GET /{key}?uploadId=U` | `max-parts` (≤ 1000), `part-number-marker`, `NextPartNumberMarker`, `IsTruncated`; per part `PartNumber`, `ETag`, `Size`, `LastModified`, checksum |
| ListMultipartUploads | `GET /?uploads` | `prefix`, `delimiter`, `key-marker`, `upload-id-marker`, `max-uploads` (≤ 1000), `encoding-type=url`; ordered by key, then by initiation time |
| GetObject / HeadObject `partNumber=N` | | Returns that part's bytes (206 with `Content-Range`), `x-amz-mp-parts-count` |

- **ETag** of the object: `hex(MD5(concat(binary MD5 of each listed part))) + "-" + partCount`.
- **Checksums**: `COMPOSITE` = checksum over the concatenated raw part checksums + `-N` (SHA1, SHA256, CRC32,
  CRC32C). `FULL_OBJECT` = the checksum of the whole object, no suffix (CRC32, CRC32C, CRC64NVME; CRC64NVME only
  supports `FULL_OBJECT`). A full-object CRC is computed from the part CRCs by **CRC combination** (no re-read).
- Size limits: 10 000 parts, object ≤ 5 TiB (≈ 48.8 TiB since 2025 is not needed here).

## 3. Design for TimeStore

**Parts are blobs; a multipart object is a manifest of them (no copy).**

- UploadPart = the existing blob write path (stream → MD5 + checksum → fsync → rename) + an `upload_parts` row.
  Re-uploading a part number replaces the row; the old blob is deleted after the commit (sweep catches failures).
- Complete = **one transaction**: validate the listed parts against `upload_parts`, write the `objects` row and its
  `object_parts` rows (part number, offset, blob, size, ETag, checksum), delete the upload and its part rows, return
  the replaced object's blobs and the unlisted parts' blobs for deletion after commit. Milliseconds whatever the
  size, so no 200-with-error streaming is needed.
- Retry of a completed upload: `objects.upload_id` records which upload produced the row; a Complete for an unknown
  upload id whose key's current row carries that id returns the stored result.
- Reads: a multipart object streams its parts in order through one concatenating stream; a range seeks to the part
  that holds its first byte (binary search over offsets); `partNumber=N` is the range of part N.
- The domain gets a closed hierarchy for object data: `SingleBlob(BlobLocator)` | `MultipartBlobs(parts)`, so
  every consumer (GET, delete, overwrite, sweep) handles both by an exhaustive switch.

**Schema v4**

- `objects` is rebuilt (SQLite cannot relax `NOT NULL`): `blob_id`/`node_id` become nullable, `size` stays (total
  size), new `parts_count INTEGER NULL` and `upload_id BLOB NULL`, `CHECK ((blob_id IS NULL) = (parts_count IS NOT
  NULL))`.
- `object_parts (version_id, part_number, offset, blob_id, node_id, size, etag, checksum)`, primary key
  `(version_id, part_number)`, `WITHOUT ROWID`; index on `blob_id`. Keyed by version so phase 6 needs no change.
- `multipart_uploads (upload_id PK, bucket_id FK, key, content_type, headers, user_metadata, checksum_algorithm,
  checksum_type, created_at)`; index `(bucket_id, key, upload_id)` for ListMultipartUploads (UUIDv7 ids sort by
  initiation time to the millisecond, as AWS orders them; uploads of one key started in the same millisecond have
  no defined order).
- `upload_parts (upload_id FK, part_number, blob_id, node_id, size, etag, checksum, created_at)`, primary key
  `(upload_id, part_number)`, `WITHOUT ROWID`; index on `blob_id`.
- ~~`DeleteBucketIfEmpty` also refuses while uploads exist.~~ **Corrected in step 4d:** only objects keep a bucket
  from being deleted; uploads in progress are aborted with it and their part blobs freed. The DeleteBucket reference
  states the "abort or complete first" rule for directory buckets only, and ceph s3-tests (run against Amazon S3) empty buckets without aborting uploads.
- `FindUnreferencedBlobsAsync` adds `object_parts` and `upload_parts`: the orphan sweep keeps working unchanged.

**Abandoned uploads.** AWS leaves them until a lifecycle rule aborts them. TimeStore has no lifecycle yet (phase 6),
so the sweep worker also aborts uploads older than `TimeStore:Multipart:AbandonAfterDays` (default 7, 0 = never),
like a built-in `AbortIncompleteMultipartUpload` rule.

**Not in phase 3:** UploadPartCopy (with CopyObject, phase 6); SSE.

## 4. Proposed phase 3 steps

| Step | Content | Proof |
|---|---|---|
| 3a ✅ | Schema v4, `ObjectData` hierarchy, upload/part/manifest store methods, sweep references, bucket deletion (corrected in 4d: uploads go with the bucket) | Contract tests (complete atomicity, replace part, concurrent complete: one wins) |
| 3b ✅ | CreateMultipartUpload, UploadPart (MD5 + checksums), AbortMultipartUpload, ListParts, ListMultipartUploads | Unit + e2e via AWS SDK |
| 3c ✅ | CompleteMultipartUpload: XML, order/part/ETag/size rules, `md5-N` ETag, composite checksum, retry, `If-None-Match` | Unit (each error) + e2e |
| 3d ✅ | Reads across parts: concatenating stream, ranges spanning parts, `partNumber`, `x-amz-mp-parts-count`; delete/overwrite free every part | e2e: ranges across part boundaries, byte-for-byte compare |
| 3e ✅ | `FULL_OBJECT` checksums: CRC32/CRC32C/CRC64NVME combination in Cryptography | Unit: combined CRC == CRC of the concatenation (random sizes); e2e with the SDK |
| 3f ✅ | Abandoned-upload cleanup in the sweep worker; e2e with `TransferUtility` (large multipart upload/download) on the AOT binary | e2e |

## 5. Decisions (2026-11-16)

1. **Manifest, no copy**: a multipart object references its part blobs (`object_parts`); Complete is one transaction.
2. **`FULL_OBJECT` checksums are in phase 3** (step 3e): CRC combination written in `TimeStore.Cryptography`.
3. **Abandoned uploads are a configuration setting**: `TimeStore:Multipart:AbandonAfterDays` (default 7, `0` =
   never), read through one options type so a lifecycle rule (phase 6) can replace it later without touching the
   cleanup code.

## 6. Notes from step 3b

- Upload ids on the wire are the UUIDv7 as 32 lower-case hex digits; anything else answers `NoSuchUpload`.
- A part's checksum must use the upload's algorithm when the upload has one (`InvalidRequest` "Checksum Type
  mismatch", as Amazon S3 words it); an upload without an algorithm still verifies and keeps a part's checksum
  (current SDKs send CRC32 on every part by default) but builds no object checksum from it.
- `upload-id-marker` is ignored without `key-marker`, as in Amazon S3.

## 7. Notes from step 3c

- Order rules follow Amazon S3: part numbers in non-decreasing order (corrected in step 4d: a repeated number is
  allowed and its last entry counts, as ceph s3-tests expect; a decrease is `InvalidPartOrder`), 1–10 000
  (`InvalidArgument`), every listed part uploaded with that ETag (`InvalidPart`, quotes optional, case-insensitive),
  every part but the last at least 5 MiB (`EntityTooSmall`). The AWS SDK sorts parts before sending, so the order
  error is covered by unit tests.
- A part checksum listed in the request must equal the stored one (`InvalidPart`); with an upload algorithm it
  must also use that algorithm.
- A part re-uploaded between reading the parts and the commit makes the store refuse (`PartsChanged`); the service
  re-reads and retries up to 3 times.
- Retried completions: when the upload is gone but the key's current object carries its upload id, the stored
  result is returned (also when a concurrent completion of the same upload won).
- Composite checksums are stored as the raw digest with type `COMPOSITE` and shown as `base64-N` (completion
  result, and GET/HEAD with `x-amz-checksum-mode: ENABLED`, which also send `x-amz-checksum-type`).
- `If-None-Match: *` is checked inside the completion transaction; `If-Match` answers `NotImplemented`, as for
  PutObject.

## 8. Notes from step 3d

- `MultipartReadStream` (Infra) is one seekable stream over the part blobs with **one part open at a time** (an
  object may have 10 000 parts). The first part is opened before the response starts, so a missing blob still
  answers before any byte; later parts open as the read reaches them, at the right offset (binary search over
  offsets).
- ~~Known limit: an overwrite or delete that frees the parts while a long GET is between parts ends that GET with an
  I/O error.~~ **Fixed in step 5c**: released blobs are deleted after a grace (60 s by default), so such a GET
  completes (end-to-end test `OverwriteWhileReadingTests`).
- `partNumber` selects a part by **its own number** (uploads may skip numbers), answers 206 with `Content-Range`
  and `x-amz-mp-parts-count`; part 1 of a single-PUT object is the whole object; an unknown part is
  `InvalidPartNumber` (416); `partNumber` with a `Range` header is `InvalidRequest`, as in Amazon S3.

## 9. Notes from step 3e

- `CrcCombine` (Cryptography) computes `crc(A‖B) = crc(A)·x^(8|B|) ⊕ crc(B)` by carry-less multiplication modulo
  the reflected polynomial; `x^(8n)` comes from a table of `x^(2^k)`, so each part costs O(log size). The identity
  holds because all three CRCs use the same value (all ones) for init and final xor. Folding starts from the CRC
  of no data, which is zero. Proven against `System.IO.Hashing` on random splits and 25-part folds for CRC32,
  CRC32C and CRC64NVME.
- A completion may carry the object checksum as an `x-amz-checksum-*` header (SDKs send it for `FULL_OBJECT`).
  The body pipeline does **not** treat that header as a checksum of the XML body on this operation
  (`headerChecksumsDescribeBody: false`); the completion compares it with the computed checksum (`BadDigest` on
  mismatch, `InvalidRequest` for another algorithm). A composite value's `-N` is dropped before comparing.
- `TransferUtility` (AWS SDK for .NET 4) uploads and downloads a 21 MiB file with its default settings.

## 10. Notes from step 3f

- `IUploadExpiryService` aborts uploads older than `UploadExpiryOptions.AbandonAfter` (from
  `TimeStore:Multipart:AbandonAfterDays`, default 7, `0` = never), oldest first in batches of 100 through
  `ix_uploads_created`, freeing their part blobs. The age is read from that one options type only, so a lifecycle
  rule (phase 6) can supply it instead.
- One `MaintenanceWorker` runs at startup and every `BlobSweep:IntervalMinutes`: abandoned uploads first, then the
  orphan-blob sweep; each is skipped when configured off, and the worker is not started when both are off.
- `TransferUtility` with default settings uploads and downloads through multipart on the Native AOT binary.
