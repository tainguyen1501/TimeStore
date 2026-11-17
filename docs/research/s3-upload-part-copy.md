# UploadPartCopy (step 6b)

Sources: the Amazon S3 API reference (UploadPartCopy: `x-amz-copy-source` with an optional `versionId`,
`x-amz-copy-source-range`, the `x-amz-copy-source-if-*` conditions, `CopyPartResult`), ceph s3-tests
(`test_multipart_copy_*`, `test_upload_part_copy_percent_encoded_key`).

## What clients expect

- `PUT /bucket/key?partNumber=N&uploadId=ID` with `x-amz-copy-source: src-bucket/src-key[?versionId=…]` stores bytes of
  an existing object as part N. The answer is `CopyPartResult` (`ETag`, `LastModified`, the part checksum when the
  upload has an algorithm) and `x-amz-copy-source-version-id` when the source is versioned.
- `x-amz-copy-source-range: bytes=first-last` (inclusive, zero-based) copies a slice; without it, the whole object.
  Only that one form is valid: anything else (`0-2`, `bytes=0`, `bytes=0-bar`, two ranges) is 400 `InvalidArgument`.
  A range past the end of the source is `InvalidRange` (s3-tests accept 400 or 416).
- One part copies at most 5 GiB (as a single PUT); the same limit as CopyObject applies to a source copied whole.
- The source conditions are evaluated against the source version that is copied; a failed one is 412.
- A delete marker as the source: latest → 404 `NoSuchKey`, named by version id → 400 `InvalidRequest`.

## Decisions (2026-11-17)

1. The part is written through the existing part path (`IUploadService.UploadPartAsync`): validation, the upload's
   checksum rule, replacement of an earlier part with the same number and blob release stay in one place. The copy
   feeds it a bounded view of the source stream (offset, length) and computes the upload's checksum algorithm while
   reading, so a part copied into an upload with a checksum algorithm carries that checksum like an uploaded one.
2. Range parsing is strict (Protocol, `S3CopySourceRange`): only `bytes=<digits>-<digits>` with first ≤ last.
3. Out-of-range answers 416 `InvalidRange`, as a GET with a range past the end does.
4. Data is copied, not shared: a part never points to another object's blob, so deleting the source later never
   touches the destination (same rule as CopyObject, step 4c).

## Notes from step 6b

- `S3CopySourceRange` (Protocol) parses the header strictly; `UploadService.UploadPartCopyAsync` checks the range
  against the source size (`UPLOAD_COPY_RANGE_OUT_OF_BOUNDS` → 416 `InvalidRange`) and the 5 GiB limit, seeks the
  source, wraps it in `SliceReadStream` (throws when the source ends early) and, for an upload with a checksum
  algorithm, in `ChecksummingReadStream`; then it calls `UploadPartAsync`.
- The handler (`S3UploadHandlers.UploadPartCopyAsync`) opens the source through `IObjectService.OpenVersionAsync`
  (versions, delete markers), evaluates `x-amz-copy-source-if-*` on it and answers `CopyPartResult` plus
  `x-amz-copy-source-version-id`.
- Tests: 14 parser cases (every ceph improper-range form), 6 end-to-end tests (ranges completing into the source
  bytes, whole object, out of range, versioned source, CRC32 upload, failed condition) and the former "not
  implemented" test now checks a missing source answers `NoSuchKey`.
