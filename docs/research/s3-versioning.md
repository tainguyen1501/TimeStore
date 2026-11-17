# S3 versioning (step 6a)

Research before code for bucket versioning and delete markers. Sources: the Amazon S3 user guide and API reference
(Using versioning, Working with delete markers, Managing objects in a versioning-suspended bucket,
PutBucketVersioning, GetBucketVersioning, ListObjectVersions, GetObject/HeadObject/DeleteObject/DeleteObjects/
CopyObject/CompleteMultipartUpload response headers) and the ceph s3-tests versioning tests
(`test_versioning_*`, `test_*_versioned_*`).

## 1. The model (Amazon S3)

- A bucket is **unversioned** (never enabled), **Enabled** or **Suspended**. Once enabled it can never return to
  unversioned. `GetBucketVersioning` answers an empty configuration for a never-versioned bucket, else its status.
  `MfaDelete` needs an MFA device: not supported here.
- Every object version has a **version id**, an opaque string. Objects written while the bucket was unversioned, and
  every write while Suspended, have the version id **`null`**; a key has at most one `null` version.
- **Enabled**: a PUT (also copy, complete multipart) adds a new version; the previous one becomes noncurrent and keeps
  its data. The response carries `x-amz-version-id`.
- **Suspended**: a PUT writes the `null` version: an existing `null` version (current or not) is replaced and its data
  is gone; a current non-null version becomes noncurrent and is kept. `x-amz-version-id: null`.
- **DELETE without a version id**: Enabled adds a **delete marker** (a version without data, with its own id) as the
  latest version; the object disappears from GET and listings. Suspended adds a `null` delete marker (replacing a
  `null` version). Unversioned deletes the object. Response: `x-amz-delete-marker: true` and `x-amz-version-id` when a
  marker was created.
- **DELETE with a version id** removes exactly that version or marker, permanently; deleting the latest makes the
  next newest version the latest (an object or a marker). `x-amz-version-id` echoes it; `x-amz-delete-marker: true`
  when it was a marker. A version that does not exist: 204 (idempotent); an invalid id: 400 `InvalidArgument`.
- **GET/HEAD without a version id**: the latest version; when the latest is a delete marker: 404 `NoSuchKey` with
  `x-amz-delete-marker: true` and the marker's `x-amz-version-id`. **With a version id**: that version
  (`x-amz-version-id`); a delete marker: 405 `MethodNotAllowed` with `x-amz-delete-marker: true` (and
  `Last-Modified`); unknown: 404 `NoSuchVersion`; malformed: 400 `InvalidArgument`.
- **ListObjects(V2)** show current objects only (no noncurrent versions, no markers).
- **ListObjectVersions**: versions and delete markers in key order, newest first within a key, with `IsLatest`;
  `key-marker` + `version-id-marker` continue (the marker version itself is excluded), `NextKeyMarker` +
  `NextVersionIdMarker` when truncated; delimiter roll-up as in listings; `null` ids are reported as `null`.
- **DeleteObjects** takes an optional `VersionId` per key; results report `VersionId`, `DeleteMarker` and
  `DeleteMarkerVersionId`.
- **CopyObject** reads `x-amz-copy-source: bucket/key?versionId=…` (a delete marker as source: 400
  `InvalidRequest`), answers `x-amz-copy-source-version-id` and the new `x-amz-version-id`.
- **DeleteBucket** fails with `BucketNotEmpty` while any version or delete marker exists.
- Conditional writes (`If-None-Match: *`) see the current object only: a delete marker means "absent".

## 3. Design for TimeStore

| Topic | Decision | Why |
|---|---|---|
| Tables | `objects` keeps the **current object** only (unchanged listing path); new `object_versions` holds every noncurrent version and every delete marker (also a latest one). Same columns, plus `is_delete_marker`, `superseded_at` | Listings and GETs stay one table and one index; version history is a separate range |
| Version identity | The internal `version_id` (UUIDv7, already the key of `object_parts`) orders versions by time; a `null_version` flag says the client-visible id is `null` | One ordering for every version, `null` included; manifests never move |
| Version id on the wire | 32 lowercase hex characters of the internal id; `null` for the null version | Opaque, fixed length, validates cheaply |
| Latest | The newest internal id among the key's rows in both tables | A replaced `null` version gets a new id, so it becomes latest, as Amazon S3 does |
| Versioning state | `buckets.versioning` (`NULL` never, `Enabled`, `Suspended`), read **inside** the write transaction | A concurrent `PutBucketVersioning` cannot split one write's decision |
| Existing data | Rows in `objects` become `null` versions | They were written unversioned, as in Amazon S3 |
| Blob release | Only when a version leaves both tables (unversioned overwrite, replaced `null` version, delete by version id) | Noncurrent versions keep their data |
| Usage metrics | `bucket_usage` gains `version_count` and `version_bytes`, kept by triggers on `object_versions` | Noncurrent versions use disk; the Metrics page shows them |
| Lifecycle (6c) | `superseded_at` is set when a version becomes noncurrent | Noncurrent expiration counts from then |
| Admin UI | Versioning toggle on the bucket page; "Show versions" in the object browser with per-version download and delete, and restore (copy a version over the key) | The operations an admin needs to recover data |

**`MfaDelete` is refused** (`NotImplemented`): it needs MFA devices TimeStore does not
have; accepting and ignoring it would promise a protection that does not exist.

## 4. Notes from step 6a

- Schema v7: `buckets.versioning`, `objects.null_version` (existing rows: 1), `object_versions` (+ blob and
  `superseded_at` indexes), `bucket_usage.version_count/version_bytes` with triggers on `object_versions`.
- One commit path for PUT, copy and completed uploads (`CommitVersionAsync`) and one delete path
  (`DeleteObjectVersionsAsync`), both deciding from `buckets.versioning` read inside the write transaction. The old
  `DeleteObjectAsync`/`DeleteObjectsAsync` store methods are thin wrappers (delete the latest).
- **UUIDv7 ordering pitfall**: .NET's `Guid.CreateVersion7()` has millisecond precision and random low bits, so two
  versions written in the same millisecond could sort the wrong way and "latest" would be wrong. A new version id is
  now `VersionOrder.Next(candidate, newest of the key)`: the candidate when it sorts later, else the newest + 1 in its
  random bits. Contract test: 20 versions written back to back list in write order.
- **Suspended writes answer no version id** (PUT, copy, complete): what ceph s3-tests
  (`test_versioning_bucket_*_return_version_id`) expect; delete markers in a suspended bucket do
  report `null`. `ObjectVersion.WriteHeader` vs `Header`.
- **Copy onto itself**: refused only when the source is the key's current version; copying an older version over its
  key is how a version is restored (found by the browser run, now an end-to-end test).
- `ListObjectVersions` merges the two tables per page (each side limited to the page size before the merge), finishes
  a key cut by a batch with a second query, rolls up common prefixes, and continues from `key-marker` +
  `version-id-marker` (`null` resolves to the key's null version).
- Admin: `PUT /api/v1/buckets/{name}/versioning`, `GET …/objects/versions`, `POST …/objects/restore`, `versionId` on
  details and downloads, `versions` in the delete request; metrics report noncurrent versions and bytes. UI: versioning
  badge and switch (suspend asks first), "Show versions" with per-version details, download, restore and delete.
- Tests: 14 contract tests, 13 end-to-end tests (AWS SDK), browser run (enable, two uploads, versions view, older
  version download, restore, delete marker and its removal, suspend). ceph s3-tests: 22 more versioning tests pass.
- Not done: versioned object ACLs, object attributes, object lock (none of them exist in TimeStore); CreateBucket on an
  existing bucket of the same owner answers 409 (two s3-tests expect the us-east-1 legacy 200).
