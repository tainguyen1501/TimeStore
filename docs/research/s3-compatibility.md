# Research: compatibility with real clients (phase 4)

Until now every end-to-end test drove TimeStore through one client, the AWS SDK for .NET. Phase 4 checks the clients
people actually use, which sign requests, frame bodies and choose checksums in their own ways. Method: run each
client against a TimeStore (Native AOT binary, fresh data directory) and record what works, what fails and why.
Probed on 2026-11-16.

## 1. What was probed

| Client | Version | How it ran | Signing / body | Result |
|---|---|---|---|---|
| AWS CLI | 2.40.3 (botocore) | Docker image `amazon/aws-cli` | SigV4, flexible checksums on by default | `mb`, `cp` up and down (20 MB → multipart), `ls`, `sync` both ways (a second sync uploads nothing), `presign` + curl, `head-object --checksum-mode` work. **Multipart uploads use CRC64NVME `FULL_OBJECT` by default**: without step 3e every large `cp` would fail. Fails: `s3 mv` (CopyObject), `get-bucket-acl` |
| rclone | 1.76.0 (AWS SDK for Go v2) | Docker image `rclone/rclone` | SigV4, own multipart | `mkdir`, `copy` up and down, `ls`, `lsd`, `check` (0 differences), `delete`, `purge` work. Fails: `copyto`/`moveto` between keys (server-side CopyObject) |
| s3cmd | 2.4.0 (own SigV4 code) | `uvx s3cmd` | SigV4 written by s3cmd itself, 5 MiB multipart | `mb`, `put` (multipart), `put --recursive`, `ls`, `info` (user metadata), `get`, `sync`, `del --recursive`, `rb` work |
| mc | — | `minio/mc`, `quay.io/minio/mc` | — | **Not runnable**: both images refuse anonymous pulls now |
| ceph s3-tests | commit `5b39871` (2026-10-12) | cloned in isolation, not run yet | boto3 | 760 tests in `test_s3.py`. Relevant to what exists: bucket listing (84), multipart (62), bucket create/delete, object read/write, `test_headers.py`. The rest needs ACLs (62), policy (47), versioning (24), lifecycle (47), SSE (48), object lock (48), CORS, tagging |

## 2. Findings

1. **Core flows work with four independent client stacks** (.NET SDK, botocore, Go SDK v2, s3cmd's own signer):
   SigV4 headers and presigned URLs, aws-chunked, every checksum default, multipart with composite and full-object
   checksums, listings, sync.
2. **CopyObject is the main gap.** It was planned for phase 6, but `aws s3 mv`, `aws s3 cp s3://… s3://…`,
   `rclone copyto/moveto` and any rename use it.
3. **Unsupported request headers are silently ignored.** `x-amz-server-side-encryption*`, `x-amz-tagging`,
   `x-amz-object-lock-*`, `x-amz-storage-class` (other than `STANDARD`), `x-amz-acl` (other than `private`),
   `x-amz-grant-*`, `x-amz-website-redirect-location` are accepted and dropped: a client believes its object is
   encrypted, locked or public when it is not. Amazon S3's rule for unsupported functionality is `NotImplemented`.
4. **mc is no longer distributed as a public image.** s3cmd already covers an independent signer and rclone the Go
   SDK.
5. **The official SigV4 test suite targets a generic service.** Many cases normalize paths (`/./`, `/../`, double
   slashes) or double-encode the path, which Amazon S3 does not do. Only the cases about header canonicalization,
   multi-value headers, query ordering and unreserved characters apply to S3.

## 3. How compatibility tests run

- **Opt-in** (`TIMESTORE_COMPAT=1`): they need Docker and `uv`, download images and packages, and take minutes.
  Normal `dotnet test` stays fast and offline.
- **Pinned**: image tags and package versions fixed in one place; a version bump is a reviewed change.
- **Same fixture** as the end-to-end tests (`TimeStoreServer`), so they also run against the AOT binary
  (`TIMESTORE_SERVER_BINARY`).
- **Nothing downloaded is committed**: s3-tests and the SigV4 suite are fetched at test time into a temporary
  directory. s3-tests is MIT-licensed; it is executed, never copied.
- **s3-tests as a ratchet**: run the relevant subset, commit the list of tests expected to pass; a listed test that
  fails breaks the run, a newly passing test is added to the list.

## 4. Phase 4 steps

| Step | Content | Proof |
|---|---|---|
| 4a ✅ | Compatibility harness: opt-in test project, pinned AWS CLI and rclone images, s3cmd via `uvx`; the scenarios of section 1 as tests | Opt-in run green on the AOT binary |
| 4b ✅ | Reject unsupported write headers (`NotImplemented`), accept default values | Unit + e2e per header |
| 4c ✅ | CopyObject (research first): copy source, `x-amz-metadata-directive`, `x-amz-copy-source-if-*`, single and multipart sources | Unit + e2e via SDK, `aws s3 mv`, `rclone moveto` |
| 4d ✅ | ceph s3-tests subset with the ratchet list | Opt-in run; baseline list committed |
| 4e ✅ | Applicable cases of the official SigV4 test suite | Opt-in run |

## 5. Decisions (2026-11-16)

1. Unsupported write headers are **rejected with `NotImplemented`**; the values that mean "default" are accepted
   (`x-amz-storage-class: STANDARD`, ...). ACLs follow Amazon S3 with ACLs disabled (revised the same day, see
   section 7): object writes accept only `bucket-owner-full-control`; CreateBucket accepts `private`.
2. **CopyObject moves into phase 4** (step 4c); UploadPartCopy stays in phase 6.
3. **mc is dropped** from the compatibility matrix.
4. Compatibility tests are an **opt-in test project** (`TIMESTORE_COMPAT=1`) on the shared `TimeStoreServer`
   fixture, with pinned images and versions; downloaded suites are never committed.

## 6. Notes from step 4a

- `Tests/TimeStore.Compat.Tests`: six tests (AWS CLI: copy small + multipart with the default CRC64NVME full-object
  checksum, sync both ways and an idempotent second sync, presign fetched by a plain HTTP client, recursive delete +
  `rb`; rclone: copy, `check`, copy back, `purge`; s3cmd: multipart `put`, `info`, `get`, `sync`, `del`, `rb`).
- A full run takes about 25 s once the images are local (first run pulls them); green on the dotnet build and on the
  Native AOT binary.
- Containers reach the host as `host.docker.internal`, added with `--add-host …:host-gateway` so Linux daemons work
  too. The scratch directory lives under the temp directory, which Docker Desktop shares by default.
- CopyObject scenarios (`aws s3 mv`, `rclone copyto/moveto`) are added with step 4c.

## 7. Notes from step 4b

- `S3UnsupportedHeaders` (Protocol, pure) runs in the dispatcher before any handler, so nothing is read or written:
  - object writes (PutObject, CreateMultipartUpload, CopyObject): SSE-S3/KMS/C, `x-amz-tagging`, object lock,
    storage classes other than `STANDARD`, `x-amz-website-redirect-location`, `x-amz-write-offset-bytes` →
    `NotImplemented` (501); `x-amz-acl` other than `bucket-owner-full-control` (`private` included) and every
    `x-amz-grant-*` → `AccessControlListNotSupported` (400), Amazon S3's answer for buckets with ACLs disabled;
  - UploadPart, UploadPartCopy, CompleteMultipartUpload, GetObject, HeadObject: SSE-C headers (the client expects its
    key to be used) → `NotImplemented`;
  - CreateBucket: object lock enabled, an ownership other than `BucketOwnerEnforced` → `NotImplemented`; an ACL
    other than the owner-only `private`, or any grant → `InvalidBucketAclWithObjectOwnership` (400).
- **ACL rule, revised:** the first version also accepted `x-amz-acl: private` on object writes. Amazon S3 does not:
  with Bucket owner enforced (the default for every new bucket), its user guide says the bucket "accepts only `PUT`
  requests that do not specify an ACL or `PUT` requests with bucket owner full control ACLs, for example, the
  `bucket-owner-full-control` canned ACL"; other ACLs fail with `AccessControlListNotSupported`.
  Accepting `private` granted nothing extra, but an application that works against
  TimeStore would then fail on Amazon S3; and modern clients no longer send it by default (Amazon S3 itself rejects
  it). Following the specification wins. On CreateBucket, Amazon S3 refuses only bucket ACLs that give access to
  someone other than the owner (`InvalidBucketAclWithObjectOwnership`), so the owner-only `private` stays accepted
  there.
- Error messages name the header, never its value (SSE-C carries a key).
- The default configurations of the AWS CLI, rclone and s3cmd send nothing that is refused (compatibility suite
  green); `aws s3 cp --sse AES256`, `--acl public-read` and `--acl private` now fail (as on Amazon S3), and
  `--acl bucket-owner-full-control` works.

## 8. CopyObject (step 4c)

**Amazon S3.** `PUT /dst` with `x-amz-copy-source: [/]bucket/key[?versionId=…]`, percent-encoded.
`x-amz-metadata-directive` `COPY` (default: content type, system headers and user metadata of the source) or
`REPLACE` (the request's); any other value is `InvalidArgument`. Copying an object onto itself without changing
anything is `InvalidRequest`. `x-amz-copy-source-if-match` / `-if-none-match` / `-if-modified-since` /
`-if-unmodified-since` are evaluated on the source; a copy has no 304, every failed condition is 412. Sources above
5 GiB need UploadPartCopy (`InvalidRequest`). Result: `CopyObjectResult` with the new ETag, LastModified and checksum.

**TimeStore.**
- `S3CopySource.Parse` decodes the **whole** value before splitting at the first slash: the AWS SDK for .NET encodes
  the separator too (`bucket%2Ffolder%2Fkey`), other clients encode only the key. Proven against the bytes the SDK
  sends (captured), and against `aws s3 mv` and `rclone moveto`.
- The handler opens the source first and checks the conditions on that open version; `IObjectCopyService` then
  copies exactly that stream (no time-of-check/time-of-use gap).
- The bytes are copied into **one new blob**, never shared: deleting either object never touches the other, and the
  orphan sweep needs no reference counts. A multipart source becomes a single-blob copy whose ETag is the MD5 of all
  bytes, as with Amazon S3. Sharing blobs (O(1) copies) would need reference counting; left for phase 5 if copies
  of large objects turn out to matter.
- The checksum is computed while copying, with `x-amz-checksum-algorithm` or else the source's algorithm.
- Destination conditions as for PutObject (`If-None-Match: *` only); unsupported headers are refused by 4b.
- No per-key permissions exist yet, so reading the source needs no separate authorization (revisit with bucket
  policies in phase 6).

## 9. ceph s3-tests (step 4d)

**Harness.** `CephS3Tests` downloads s3-tests at commit `5b39871` (MIT; executed, never committed), creates the
`alt` and `tenant` users as access keys through the admin API, writes `s3tests.conf` (user ids = access key ids, the
owner TimeStore reports; no `api_name`, a Ceph zonegroup name), runs `test_s3.py` and `test_headers.py` through
`uvx … pytest` (an ephemeral environment) without the markers of missing features and `fails_on_aws`, reads the
JUnit report and compares it with `Ceph/expected-pass.txt`. About 50 s per run. The upstream README says the suite
moved into the ceph/ceph repository; the pinned commit stays usable.

**Baseline:** 200 tests pass, identical over two runs (from 145 on the first run). The ratchet was checked both ways
(a listed test that fails breaks the run with its name and message).

**TimeStore bugs it found, fixed in this step:**

| Finding | Fix |
|---|---|
| Teardown lists versions: ListObjectVersions answered `NotImplemented`, so every test failed at setup | ListObjectVersions for never-versioned buckets: each object is its only version (`null`, latest), paging by key marker |
| A key made only of spaces (`<Key> </Key>`) was read as empty: DeleteObjects answered `MalformedXML` | The XML reader keeps whitespace; parsers skip non-element nodes anyway |
| DeleteBucket refused a bucket with uploads in progress (177 cascading failures) | Only objects count; uploads are aborted with the bucket and their parts freed (corrects step 3a) |
| Non-ASCII user metadata made GET answer 500 (Kestrel writes ASCII headers) | Metadata values go back as Latin-1 bytes when Latin-1 can carry them, RFC 2047 (`=?UTF-8?B?…?=`) otherwise; requests are still read as UTF-8 (reading them as Latin-1 broke signatures) |
| An error answered before an `Expect: 100-continue` body was read left the body on the connection, and the next request failed with 400 | Such errors close the connection (`Connection: close`) |
| A presigned URL with `X-Amz-Expires` ≤ 0 was `AuthorizationQueryParametersError` (400) | It has already expired: 403 `AccessDenied` "Request has expired" |
| `continuation-token=` (empty) was `InvalidArgument` | An empty token is no token, echoed back empty |
| ListObjects v1 URL-encoded the top-level `Prefix` with `encoding-type=url` (SDKs decode it only in v2) | Echoed as sent; encoded only when XML cannot carry it |
| An undecodable object checksum on Complete was `InvalidRequest` | `BadDigest` |
| A part number listed twice in Complete was `InvalidPartOrder` | Non-decreasing order; the last entry of a number counts (corrects step 3c) |

**Known differences left on purpose or for later:**
- `partNumber` beyond the last part: s3-tests expects 400 `InvalidPart`; TimeStore answers 416 `InvalidPartNumber`,
  like the Amazon S3 error description ("The requested partnumber is not satisfiable").
- `Transfer-Encoding: chunked` PUT without a length (`MissingContentLength` today): needs a write path for bodies of
  unknown length.
- Conditional writes with `If-Match` (PutObject, CompleteMultipartUpload): `NotImplemented` today; Amazon S3 supports
  them. Good candidate for phase 6.
- C1 control characters in a key (`\xae\x8a-`): Amazon S3 answers 400 "Couldn't parse the specified URI.", TimeStore
  treats the key as valid.
- Recreating one's own bucket in us-east-1, ListBuckets pagination (`max-buckets`), GetObjectAttributes,
  read-only GetBucketAcl/GetObjectAcl: not implemented.
- Tests that edit `Authorization`/`Content-Length` in a `before-call` hook (before signing): the SDK re-adds them, so
  the tests cannot fail as intended (marked `fails_on_rgw` upstream); not TimeStore bugs.
- Isolation between users (`alt` cannot see `main`'s buckets): every access key sees every bucket until IAM exists.

## 10. The official SigV4 test suite (step 4e)

**Where it lives now.** The zip once linked from the AWS General Reference
(`…/gr/samples/aws-sig-v4-test-suite.zip`) answers 404. The maintained copy is
`tests/aws-signing-test-suite/v4` in awslabs/aws-c-auth (Apache-2.0; last changed in commit `3281f86`,
2024-09-05): 38 cases, each with `context.json` (credentials, region, service `service`, `normalize`,
`omit_session_token`) and, for both the header and the query (presigned) form, the expected canonical request,
string to sign, signature and signed request. Downloaded at test time, never committed.

**Which cases apply to S3.** The suite targets a generic service, which normalizes paths; S3 does not. Its
`-unnormalized` variants are exactly the S3 rules (path encoded once, `//`, `.` and `..` kept), so the test runs every
`normalize: false` case and every normalized case whose path normalization changes nothing: **32 cases**. Left out:
the six `*-normalized` cases with dot segments or repeated slashes, and the query form of `post-sts-header-after`
(`omit_session_token`: the token is added after signing, which S3 never accepts; its signed form is covered by
`post-sts-header-before`).

**How.** For each case and form, `SigV4Canonical` builds the canonical request from the published signed request,
then the string to sign (scope with the suite's service), and `SigV4Signer` the signature; all three must equal the
published files. The only production change: `DeriveSigningKey` and `Scope` take an optional service (default `s3`;
the server never passes another).

**Result:** all 32 cases match in both forms (63 checks): header value trimming and folding, repeated header names,
header order and case, query ordering by encoded name and value, unreserved characters, UTF-8 paths and queries,
empty query keys and values, session tokens, form bodies.
