---
paths:
  - "backend/Src/TimeStore.Protocol/**"
  - "backend/Src/TimeStore.Server/S3/**"
---

# S3 protocol rules

- **Amazon S3 is the specification.** Never copy or translate code from other projects. Follow the workflow:
  research note → C# decision → roadmap → code.
- `TimeStore.Protocol` has no ASP.NET or storage dependency: it parses (request line, headers, query, SigV4,
  `aws-chunked` bodies, XML bodies) and writes (XML responses, errors). The Server's S3 handlers glue Protocol to
  services.
- Routing: path style (`/bucket/key`) and virtual-host style (`bucket.host/key`); the operation is decided by method +
  sub-resources in the query (`?uploads`, `?partNumber=`, `?list-type=2`, ...). Model the operations as a
  `closed record` hierarchy so the dispatcher switch is exhaustive.
- Keys: UTF-8, ≤ 1024 bytes, validated with `Utf8.IndexOfInvalidSubsequence`. Listing order is binary UTF-8 order.
- Errors: every `AppError` code used on the S3 port maps to an `S3ErrorCodes` value + HTTP status (add new catalogs to
  `Tests/TimeStore.Server.Tests/Conventions/S3ErrorMapTests`); XML via
  `S3ErrorXml`; `RequestId` = trace identifier.
- Request XML is read with `S3XmlReader` only: no DTD, no resolver (XXE), bounded size, read to the end so body
  verification completes before the document is used.
- XML: `XmlWriter`/`XmlReader` only; S3 namespace `http://s3.amazonaws.com/doc/2006-03-01/` on responses that have it.
- Request bodies: `S3RequestBody.Open` decides how a body is verified (aws-chunked in three modes with chunk and
  trailer signatures, `x-amz-content-sha256`, `x-amz-checksum-*`); handlers read `Request.Body` and get the payload
  only. Never bypass it or read the raw body. Verification completes in the read that returns the last payload byte
  (`PayloadLength`), so a handler may stop after exactly that many bytes; keep this guarantee (`ExactLengthReadTests`).
- Authentication: SigV4 header and presigned query; `aws-chunked` streaming signatures (`STREAMING-AWS4-HMAC-SHA256-PAYLOAD`)
  and unsigned payload; compare signatures with `FixedTimeEquals`; never log signatures or presigned URLs.
- Headers: ETag quoted (`"md5hex"`, multipart `"md5hex-N"`), `Last-Modified` in RFC 1123, `x-amz-request-id`.
- Every operation ships with an end-to-end test through the AWS SDK for .NET against the running Server
  (`Tests/TimeStore.Server.Tests/S3/`), passing on the AOT binary too, and is marked in the operation table of
  `docs/roadmap.md`.
- **Never drop a request header silently.** A header asking for something TimeStore cannot do (encryption, ACLs,
  object lock, tags, other storage classes, ...) is refused before the body is read (`S3UnsupportedHeaders`):
  `NotImplemented`, or the Amazon S3 codes for buckets with ACLs disabled (`AccessControlListNotSupported` on object
  writes, which accept only `bucket-owner-full-control`; `InvalidBucketAclWithObjectOwnership` on CreateBucket,
  which accepts the owner-only `private`). Only values that mean the default pass (`STANDARD`,
  `BucketOwnerEnforced`, object lock `false`). When a feature lands, take
  its headers out of that list in the same change. Error messages name the header, never its value.
- SigV4 canonicalization follows S3: the path is encoded once and never normalized (no `//`, `.` or `..` folding).
  The official suite's `-unnormalized` cases are the S3 rules; keep `SigV4TestSuiteTests` green when changing
  `SigV4Canonical`. `SigV4Signer.DeriveSigningKey`/`SigV4Canonical.Scope` take a service only for that suite: the
  server always signs for `s3`.
