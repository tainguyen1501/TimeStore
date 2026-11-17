# Bucket policies (step 6d)

Sources: the Amazon S3 user guide (Bucket policies, Policy actions, Policy resources, Condition keys, How Amazon S3
authorizes a request, Blocking public access) and the IAM JSON policy reference (elements, evaluation logic,
condition operators); ceph s3-tests (`bucket_policy` marker: `test_bucket_policy*`, `test_set_get_del_bucket_policy`).

## 1. The model (Amazon S3)

- A bucket has at most one policy, a JSON document of up to 20 KB: `Version` (`2012-10-17`, or the older
  `2008-10-17`), optional `Id`, and `Statement`s with `Sid`, `Effect` (`Allow`/`Deny`), `Principal` (or
  `NotPrincipal`), `Action` (or `NotAction`), `Resource` (or `NotResource`) and optional `Condition`.
- Actions are `s3:<Name>` with `*` and `?` wildcards (`s3:*`, `s3:Get*`). Resources are ARNs:
  `arn:aws:s3:::bucket` for bucket actions (`s3:ListBucket`), `arn:aws:s3:::bucket/key` for object actions
  (`s3:GetObject`), with wildcards. A policy must only name its own bucket (else `MalformedPolicy`).
- **Evaluation**: an explicit `Deny` that matches wins; otherwise an `Allow` from the bucket policy or from the
  caller's own (identity) permissions allows; otherwise the request is denied. Callers of the bucket owner's account
  are allowed by their identity policies; callers from other accounts and anonymous callers need the bucket policy.
- `Principal: "*"` (or `{"AWS": "*"}`) includes anonymous requests: that is how a bucket is made public.
- Conditions compare request context keys (`aws:SourceIp`, `aws:SecureTransport`, `s3:prefix`, …) with operators
  (`StringEquals`, `StringLike`, `IpAddress`, `Bool`, …, each with an `…IfExists` form).
- `GetBucketPolicy` without a policy: 404 `NoSuchBucketPolicy`; the document comes back as it was stored.
  `GetBucketPolicyStatus` reports whether the policy makes the bucket public.

## 2. TimeStore today

Single tenant: the root key and every stored access key have full S3 access (like IAM users of one account with
`s3:*`). There is no second account and no anonymous access. ceph s3-tests' "alt" user stands for another account,
which has no access until a bucket policy grants it.

## 3. Decisions (2026-11-17)

| Topic | Decision |
|---|---|
| Key scope | Every access key gets an S3 scope: `full` (default, today's behaviour: allowed unless a bucket policy denies) or `policy` (allowed only where a bucket policy allows it, like a principal from another account). This makes policies useful for sharing one bucket with one key, and lets the ceph "alt" user be what the tests mean |
| Root key | The root key ignores bucket policies, so a wrong policy can always be fixed (break-glass, as the root key already is for everything else) |
| Evaluation | explicit Deny > Allow > default (full keys allowed; policy keys and anonymous callers denied); `AccessDenied` (403) |
| Principals | `"*"`, `{"AWS": "*" \| "<access key id>" \| "arn:aws:iam::<account>:user/<access key id>" \| [...]}`; `NotPrincipal` is refused (`NotImplemented`) |
| Actions | `s3:` actions TimeStore has, with `*`/`?`; `NotAction`; a request maps to one action (`s3:GetObject`, `s3:GetObjectVersion` with a version id, `s3:ListBucket`, `s3:PutObject` for every write including multipart parts and copies, …) |
| Resources | ARNs with wildcards, `NotResource`; every resource must be able to name this bucket, else `MalformedPolicy` |
| Conditions | operators `StringEquals`, `StringNotEquals`, `StringLike`, `StringNotLike`, `IpAddress`, `NotIpAddress`, `Bool`, `NumericEquals`/`NumericLessThan(Equals)`/`NumericGreaterThan(Equals)`, each also `…IfExists`; keys `aws:SourceIp`, `aws:SecureTransport`, `aws:username` (the access key id), `s3:prefix`, `s3:delimiter`, `s3:max-keys`, `s3:versionid`. An unknown operator or key is refused at PUT (`MalformedPolicy`) instead of being silently skipped |
| Anonymous | Unsigned requests are let through to the policy check (they used to be refused at authentication): allowed exactly when a statement with principal `*` allows them; their bodies are unsigned payloads (no aws-chunked) |
| Multi-object delete | Checked per key (`s3:DeleteObject`); denied keys are reported as `AccessDenied` entries |
| Storage and speed | `bucket_policies` (migration v9: bucket id, document, updated at) and `access_keys.s3_scope`; parsed policies are cached per bucket and replaced on PUT/DELETE; a request without a policy and with a full key costs one dictionary lookup |
| Admin | `GET/PUT/DELETE /api/v1/buckets/{name}/policy` (the JSON text), key scope on create and as a change; UI: policy editor with a "public" badge, scope on the access keys page |

## 4. Notes from step 6d

- Domain: `AccessKey.S3Scope` (`AccessKeyScopes.Full` / `Policy`); store `Get/Set/DeleteBucketPolicyAsync`,
  `SetAccessKeyScopeAsync` (migration v9, contract tests).
- `BucketPolicy` (Application) parses and evaluates; `BucketPolicyService` stores and caches; `S3Authorizer` decides
  (root → allowed; Deny → denied; Allow → allowed; else full keys only). The condition context is built only when a
  policy is evaluated.
- S3 pipeline: authentication lets a request with no signature at all go on as anonymous (an unsigned payload); the
  new authorization step maps each operation to its action and resource (`S3Authorization.For`, an exhaustive
  switch) and answers `AccessDenied` before any body is read. DeleteObjects checks each key (denied keys become
  `AccessDenied` entries), CopyObject and UploadPartCopy check `s3:GetObject`/`s3:GetObjectVersion` on the source.
- **Anonymous requests for unsupported operations answer `AccessDenied`** (as before 6d)
  instead of `NotImplemented`: no policy can grant them, and an unsigned caller learns nothing. Found by ceph
  `test_post_object_*` (browser-form POST uploads carry no SigV4 header).
- ceph s3-tests: the `bucket_policy` marker runs; +9 tests (255). The "alt" user stays a full-scope key: s3-tests
  set up and clean up through it (ListBuckets, its own buckets), which a policy-scope key may not do in a
  single-tenant store. Policy-scope behaviour is covered by the end-to-end tests instead.
- AWS SDK v4 does not throw for `NoSuchBucketPolicy`: it returns the 404 error document as the policy text (checked
  in the end-to-end test).
- Cost: warp (Native AOT, osx-arm64, 15 s per workload, root key) before/after: every workload within run-to-run
  noise (PUT 1 KiB ×1: 128 vs 127 obj/s; GET 64 KiB ×16: 12 993 vs 12 772 obj/s). The root key skips the check;
  a full key on a bucket without a policy costs one credential-cache and one dictionary lookup.
- Tests: 29 unit (parse, evaluate, conditions, glob), 2 contract, 14 end to end (anonymous public read and write,
  Deny on a full key, root bypass, policy-scope key, scope change, copy source, per-key multi-delete, admin API,
  malformed and `NotPrincipal`); browser run (policy editor, Public badge, server errors, key scope on create and
  in the menu).
