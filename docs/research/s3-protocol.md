# S3 protocol (phase 1): research and C# design

Status: **implemented** (phase 1 done, 2026-11-16). AWS documentation is the specification.

Covers: request routing, SigV4 (header and presigned), `aws-chunked` streaming bodies and checksums, errors and
response conventions. Object storage behaviour (PUT/GET semantics, listing) is phase 2.

---

## 1. Routing

### What AWS specifies

- **Path style** `https://host/bucket/key` and **virtual-hosted style** `https://bucket.host/key`. AWS prefers
  virtual-hosted; path style still works and is what self-hosted clients usually use (`--endpoint-url`,
  `force_path_style`).
- The operation is decided by **method + whether there is a key + sub-resources in the query string**
  (`?uploads`, `?uploadId=`, `?partNumber=`, `?versioning`, `?list-type=2`, ...) + one header:
  `x-amz-copy-source` turns a `PUT` object into `CopyObject` / `UploadPartCopy`.
- Keys are any UTF-8 up to **1024 bytes**; `a/../b`, `//x`, trailing `/` are valid keys. Keys arrive
  percent-encoded in the path.

### Best practice / decisions for TimeStore

| Topic | Decision |
|---|---|
| Model | `closed record S3Operation` with intermediate `closed` `BucketOperation` / `ObjectOperation`; each leaf carries its parsed parameters (list params, uploadId, partNumber, versionId). Exhaustive `switch` in the dispatcher. |
| Parsing | One pass over the **raw request target** (`IHttpRequestFeature.RawTarget`), not ASP.NET's decoded `Path` (it decodes `%2F` inconsistently and loses the exact bytes SigV4 needs). |
| Decoding | Own percent-decoder to UTF-8 bytes, then `Utf8.IsValid`; invalid encoding → `InvalidURI`. Key byte length checked before allocating the string (> 1024 → `KeyTooLongError`). |
| Virtual host | `TimeStore:S3:Domains` list; `bucket.<domain>` → virtual-host; anything else → path style (catch-all). Port stripped before matching. |
| Ambiguity | More than one recognised operation sub-resource → `InvalidRequest`. |
| Unsupported sub-resources | Known list (`acl`, `tagging`, `policy`, `cors`, `lifecycle`, ...) → `UnsupportedOperation` → `NotImplemented` (501), never mistaken for a plain GET/PUT. |
| Key safety | No `..`/`.` rejection: keys never become paths in TimeStore (blobs by id). |
| Errors | `MethodNotAllowed` (405) for a method that does not apply; `InvalidArgument` for bad `max-keys`, `partNumber` (1–10000), `encoding-type` ≠ `url`, `list-type` ≠ 2. |

---

## 2. SigV4 authentication

### What AWS specifies

- **Header auth**: `Authorization: AWS4-HMAC-SHA256 Credential=<key>/<yyyymmdd>/<region>/s3/aws4_request,
  SignedHeaders=a;b;c, Signature=<hex>`; time from `x-amz-date` (or `Date`).
- **Canonical request** = method, canonical URI, canonical query, canonical headers, signed header list, payload
  hash. For S3 the URI is encoded **once** (no double encoding, unlike other services); `/` kept.
- **String to sign** = `AWS4-HMAC-SHA256`, timestamp, scope, SHA-256 of canonical request. **Signing key** =
  HMAC chain `AWS4+secret` → date → region → `s3` → `aws4_request`.
- **`x-amz-content-sha256` is required** for S3 header auth: a hex SHA-256 of the body, `UNSIGNED-PAYLOAD`, or a
  `STREAMING-*` mode (section 3). When it is a real hash, the server must check the body against it
  (`XAmzContentSHA256Mismatch`).
- **Presigned URLs**: `X-Amz-Algorithm`, `X-Amz-Credential`, `X-Amz-Date`, `X-Amz-Expires` (0–604800 s),
  `X-Amz-SignedHeaders`, `X-Amz-Signature` in the query; payload is `UNSIGNED-PAYLOAD` unless
  `X-Amz-Content-Sha256` is in the query.
- Clock skew: requests more than 15 minutes from server time → `RequestTimeTooSkewed` (403).

### Decisions for TimeStore

| Topic | Decision |
|---|---|
| Location | `TimeStore.Protocol/Auth/`: parser (`SigV4Header`, `SigV4Query`), `CanonicalRequest` builder, `SigV4Verifier`. No ASP.NET types: input is method, raw target, a header lookup, a clock. |
| Allocation | Canonical request built into a pooled UTF-8 buffer and hashed incrementally; HMAC with `HMACSHA256.HashData` into `stackalloc` spans; hex via `Convert.ToHexStringLower`. |
| Signing key cache | Cache per (access key, date, region): one entry per key per day, so per-request cost is one HMAC chain less. |
| Comparison | `CryptographicOperations.FixedTimeEquals` on the decoded bytes. |
| Unsigned `x-amz-*` headers | Rejected. |
| Proxies | Phase 1: sign against the request's own `Host`. A `TimeStore:S3:PublicHost` override and trusted-proxy forwarded headers come later (noted in roadmap), never trusting `X-Forwarded-*` by default. |
| Payload hash | Hex value → verified while streaming the body (hash on the way, compare at the end, fail before the metadata commit). |
| Credentials | Phase 1: one root key from configuration (`TimeStore:S3:RootAccessKey` / `RootSecretKey`), behind an `ICredentialStore` port so phase 2 swaps in stored, encrypted keys. |
| Anonymous | Not supported in phase 1: no `Authorization` and no presign → `AccessDenied`. |
| SigV2 | Not supported (`SignatureVersionNotSupported` style error); AWS deprecated it. |

---

## 3. `aws-chunked` streaming bodies and checksums

### What AWS specifies

`x-amz-content-sha256` values for streaming uploads (`Content-Encoding: aws-chunked`,
`x-amz-decoded-content-length` = real payload size):

| Mode | Chunks signed | Trailer |
|---|---|---|
| `STREAMING-AWS4-HMAC-SHA256-PAYLOAD` | yes | no |
| `STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER` | yes | yes, **signed** (`AWS4-HMAC-SHA256-TRAILER` string to sign) |
| `STREAMING-UNSIGNED-PAYLOAD-TRAILER` | no | yes, unsigned |

- Chunk format: `hex-size;chunk-signature=<sig>\r\n<data>\r\n`, ending with a 0-size chunk. Each chunk signature
  chains from the previous one (seed = the request signature): string to sign =
  `AWS4-HMAC-SHA256-PAYLOAD`, timestamp, scope, previous signature, SHA-256(""), SHA-256(chunk data).
- Trailer: `x-amz-checksum-<algo>:<base64>` lines (declared up front in `x-amz-trailer`), then for the signed mode
  `x-amz-trailer-signature:<sig>`.
- Checksum algorithms: CRC32, CRC32C, CRC64NVME, SHA1, SHA256.

### Why it matters now

**Verified 2026-11-16:** the AWS SDK for .NET 4.1 sends `PutObject` over HTTP as
`STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER` with `Content-Encoding: aws-chunked` and `x-amz-trailer:
x-amz-checksum-crc32` (pinned by `SigV4AwsSdkTests`). So the signed-trailer mode, including its trailer
signature, is the default path for .NET clients.

Current AWS SDKs and the AWS CLI default to `request_checksum_calculation = when_supported`: uploads send
**`STREAMING-UNSIGNED-PAYLOAD-TRAILER` with a CRC checksum trailer**. S3-compatible servers that do not support it
break with recent clients (documented for NetApp ONTAP; Go SDK v2 switched its default in v1.73.0). TimeStore must
support all three modes and the checksum algorithms in phase 1, not later.

### Decisions for TimeStore

| Topic | Decision |
|---|---|
| Location | `TimeStore.Protocol/Streaming/AwsChunkedReader`: a `Stream` (read side) over a `PipeReader`, so services stream it into `IBlobStore` unchanged. |
| Verification | All three modes; chained chunk signatures; **trailer signature verified** in the signed-trailer mode. Signing key computed once per request. |
| Robustness | Max header line 4 KiB; chunk size parsed as hex without allocation; tolerate a missing final CRLF; reject size mismatch with `x-amz-decoded-content-length` (`IncompleteBody`). |
| Checksums | In `Core/TimeStore.Cryptography` behind `IChecksumFactory`: CRC32 (`Crc32`), CRC32C (`Crc32ParameterSet.Crc32C`), CRC64NVME (`Crc64ParameterSet.Nvme`) from **`System.IO.Hashing` 11.0.0** (verified in the package), SHA1/SHA256 (`IncrementalHash`). Also accept checksums as plain headers (`x-amz-checksum-*`) on non-streaming uploads. Mismatch → `BadDigest`. |
| Storing checksums | Phase 2 stores the object checksum and returns it (`x-amz-checksum-*` on GET/HEAD when requested). |

---

## 4. Errors and response conventions

### What AWS specifies

- Error body: `<Error><Code/><Message/><Resource/><RequestId/></Error>` (some errors add `BucketName`, `Key`, ...);
  `Content-Type: application/xml`. `HEAD` responses carry the status only, no body.
- Every response carries `x-amz-request-id` (and `x-amz-id-2`); success XML documents use the namespace
  `http://s3.amazonaws.com/doc/2006-03-01/`.
- `GetBucketLocation` returns an empty `LocationConstraint` for `us-east-1`.

### Decisions for TimeStore

| Topic | Decision |
|---|---|
| Error map | Protocol errors are `AppError`s from `S3RequestErrors` / `S3AuthErrors` catalogs; a single `S3ErrorMap` (Server) maps every `AppError` code used on the S3 port (protocol + service catalogs) to S3 code + status; unknown → `InternalError`. A convention test checks every catalog code on the S3 path has a mapping. |
| Writer | `S3Responses` / `S3Errors`: `x-amz-request-id` = trace id, `x-amz-id-2`, XML via `XmlWriter`, no body for `HEAD`/304. **No manual draining**: Kestrel consumes an unread request body and keeps the connection alive (verified 2026-11-16: two 1 MiB PUTs answered 403 before reading, second one reused the connection). |
| XML documents | Hand-written writers per document (`ListAllMyBucketsResult`, `ListBucketResult`, `LocationConstraint`, ...) with the S3 namespace; readers for request bodies (`CreateBucketConfiguration`, later `Delete`, `CompleteMultipartUpload`) with `XmlReader`, size-limited. |

---

## 5. Pipeline: what runs as middleware

Validation that needs only the request line and headers runs as **thin middlewares on the S3 port**, before the
dispatcher. Each one calls Protocol (pure, testable) and stops the request with an S3 error on failure:

```
S3RequestIdMiddleware      x-amz-request-id / x-amz-id-2 on every response
S3RoutingMiddleware        raw target → S3Operation (or InvalidURI / MethodNotAllowed / NotImplemented)
S3AuthMiddleware           SigV4 header or presigned: parse, clock skew, expiry, unsigned x-amz-* check,
                           credential lookup (ICredentialStore), signature → authenticated principal
S3BodyMiddleware           wraps Request.Body: aws-chunked decoder (3 modes) or payload-hash verifier,
                           plus checksum verification; size limits (decoded length, max object size)
S3Dispatcher               switch over S3Operation → services → S3ResponseWriter
```

Body checks cannot finish in a middleware (the body is read later by the handler), so `S3BodyMiddleware` replaces
`Request.Body` with a verifying stream: the hash/signature/checksum is compared when the stream reaches its end, and
a mismatch surfaces as an error before the metadata commit (blob written, metadata not committed, orphan cleaned).

## 6. Caching

- **`ICacheService`** (Application abstraction) for credentials (phase 2, keys from the database) and bucket
  lookups. Implemented in Infra by `MemoryCacheService` (`IMemoryCache` + single-flight + tags). HybridCache was
  planned, but **11.0.0 fails Native AOT publish** (IL2026 inside the library, verified): the interface lets us switch
  to it, or add a distributed level for multi-node, in one place later.
- **Signing keys** (SigV4): derived deterministically from secret + date + region, recomputed per request by most
  servers. Kept in a tiny in-process memo inside `TimeStore.Protocol` (bounded, keyed by access key + date + region,
  cleared daily), not in the cache service: no serialization, no async, and Protocol stays free of Application
  dependencies.

## 7. Configuration (proposed)

```json
"TimeStore": {
  "S3": {
    "Region": "us-east-1",
    "Domains": [],
    "RootAccessKey": "",
    "RootSecretKey": "",
    "MaxClockSkewSeconds": 900
  }
}
```

- `Region`: the region clients sign with and `GetBucketLocation` reports.
- `Domains`: enables virtual-host routing for `bucket.<domain>`; empty = path style only.
- `RootAccessKey` / `RootSecretKey`: required on startup (no built-in default secret); `appsettings.Development.json`
  carries a development pair. Secret ≥ 8 characters, never logged.

## 8. Proposed phase 1 steps

| Step | Content | Proof |
|---|---|---|
| 0 ✅ | `Core/TimeStore.Cryptography`: `IChecksumFactory` (CRC32/32C/64NVME/SHA1/SHA256), `Signing` (HMAC-SHA256, SHA-256, constant-time compare); `ICacheService` + `MemoryCacheService` | Known-answer tests (`"123456789"` check values, RFC 4231), single-flight test, AOT publish clean |
| 1a ✅ | Raw-target parser, percent-decoder, `S3Operation` hierarchy, router (path + virtual host), `S3RequestErrors`, dispatcher switch | Unit tests per operation and edge case (encoded keys, `+`, `//`, 1024-byte keys, ambiguous sub-resources) |
| 1b ✅ | `S3AuthMiddleware` + SigV4 header + presigned, signing-key cache, unsigned-header check, clock skew, payload hash check, `ICredentialStore` with root key | AWS documentation worked examples + signatures produced by the AWS SDK for .NET in tests |
| 1c ✅ | `S3BodyMiddleware`, `AwsChunkedStream` (3 modes, trailer signature), checksum verification | AWS documentation chunked examples + uploads from the AWS SDK for .NET with each checksum algorithm |
| 1d ✅ | `S3Responses`, `S3ErrorMap` + convention test, XML writers/readers for bucket operations | XML compared with AWS documentation samples |
| 1e ✅ | Bucket operations over S3: ListBuckets, CreateBucket, DeleteBucket, HeadBucket, GetBucketLocation | `aws s3 mb/ls/rb` and the AWS SDK for .NET against the running Server (and the AOT binary) |

## 9. Decisions on open questions (2026-11-16)

1. **Test oracle for SigV4**: phase 1 uses the AWS documentation examples plus the AWS SDK for .NET (`AWSSDK.S3`,
   test-only dependency, used as a client) to produce expected signatures. The official `aws-sig-v4-test-suite` is run
   **later (phase 4)** as an extra check, downloaded at test time into a cache directory and never committed (opt-in
   test), so no test data is copied into the repository. Done in step 4e: the original zip is gone (404); the
   maintained copy in aws-c-auth is used (`docs/research/s3-compatibility.md`, section 10).
2. **CRC64NVME / CRC32C**: no own implementation: `System.IO.Hashing` 11.0.0 ships `Crc64ParameterSet.Nvme` and
   `Crc32ParameterSet.Crc32C`. Verify AOT publish when the package is added.
3. **Crypto code** lives in a reusable `Core/TimeStore.Cryptography` project (no S3 knowledge); SigV4 itself stays
   in Protocol because it is S3/AWS-specific, built on Cryptography.

## Sources

- Amazon S3 API: SigV4 [Authorization header](https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-auth-using-authorization-header.html),
  [streaming trailers](https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-streaming-trailers.html)
- AWS SDKs: [data integrity protections for Amazon S3](https://docs.aws.amazon.com/sdkref/latest/guide/feature-dataintegrity.html)
  (`request_checksum_calculation`)
- Third-party breakage reports: [Percona forum (NetApp ONTAP)](https://forums.percona.com/t/allow-configuring-s3-request-checksum-calculation-blocks-pbm-on-netapp-ontap-s3/41147),
  [GitLab Runner MR](https://gitlab.com/gitlab-org/gitlab-runner/-/merge_requests/6691)
- SigV4 test suite layout: [botocore test_sigv4.py](https://sources.debian.org/data/main/p/python-botocore/1.40.72+repack-1/tests/unit/auth/test_sigv4.py)

