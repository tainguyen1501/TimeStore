# Replication

Replication copies every change of a bucket to another S3 endpoint (MinIO, AWS S3, Cloudflare R2, Backblaze B2,
another TimeStore) a few seconds after it happens. The destination is an ordinary bucket: if this server is lost,
point your clients at the destination.

> Only each key's **current** state is copied: older versions of a versioned bucket are not (planned). Objects carry
> no replication status yet (`x-amz-replication-status`, planned).

## How it works

- Every write (upload, copy, multipart upload, delete, lifecycle expiration) records "this key changed" in the
  database **in the same transaction** as the write. A crash or a redeploy never loses a change: the server sends it
  after the restart.
- A background worker sends the key's current state: the content with its content type, headers and user metadata,
  with `Content-MD5` so the destination refuses a damaged copy. A key changed 100 times while waiting is sent once.
- Uploads are answered first; replication never slows them, and a destination that is down never fails one.
- **Destination down** (network error, timeout, 5xx, throttling): replication **pauses** and tries again after 1, 2,
  4 … seconds, up to 15 minutes, for as long as it takes. Nothing is lost or marked failed.
- **Destination refuses** (wrong credentials, missing bucket, an object it rejects): those changes are marked
  **failed** and wait for you: fix the cause, then **Retry now**.
- **Deletes** are not copied unless you turn it on: a mistake on this server is not copied.

## Set it up

You need, at the destination: a bucket (create it first) and an access key allowed to write and delete objects in
it.

1. **Buckets** → open the bucket → **⋯** → **Replication** → **Set up replication**.
2. Fill in:
   - **Endpoint**: the destination's S3 address, `https://host[:port]`, for example
     `https://<account>.r2.cloudflarestorage.com` or `https://s3.eu-central-1.amazonaws.com`. It is reached
     path-style (`https://host/bucket/key`).
   - **Destination bucket**, **Region** (`us-east-1` if unsure; Cloudflare R2 uses `auto`), **Access key**,
     **Secret key**.
   - **Only keys starting with**: leave empty for the whole bucket, or a prefix such as `photos/`.
   - **Delete at the destination too**: off by default.
   - **Copy the objects already in the bucket too**: on by default; off sends only changes from now on.
3. **Test and save**. TimeStore first writes and deletes a small test object (`.timestore-replication-test/…`) at
   the destination. If that fails, nothing is saved and the form shows the destination's answer, for example
   `SignatureDoesNotMatch (403)` (wrong secret) or `NoSuchBucket (404)`.

The secret key is stored encrypted with the master key and never shown again. To change the rule, **Edit**: leave
the secret empty to keep it (same endpoint and access key).

## Watch it

The sheet refreshes every few seconds while it is open:

| Badge | Meaning |
|---|---|
| **Up to date** | Nothing waits |
| **N waiting** | Changes are being sent; **Waiting** shows how old the oldest one is (how far behind the destination is) |
| **Paused** | The destination failed for a temporary reason; the line below says why and when it is tried again |
| **N failed** | The destination refused these changes; the list shows each key and the reason |

**Last sent** is when a change last reached the destination. The metrics (`GET /api/v1/metrics`, `replication`) list
every rule with the same numbers.

Actions (admins):

- **Retry now**: failed changes are sent again, and a paused rule tries at once.
- **Test**: writes and deletes a test object with the saved settings.
- **Send everything again**: every object (under the prefix) is copied again, for a destination that lost data or
  was replaced. Objects already there are overwritten with the same content; nothing is deleted.
- **Remove**: changes are no longer copied; what still waits is dropped. Nothing is deleted at the destination.

## Good to know

- Never point a rule at its own bucket, or two servers at each other: two-way replication is not supported yet.
- A backup archive holds no replication rules: after a restore, set them up again on purpose, so an older state is
  never pushed over a newer copy.
- A large object is sent the way it was uploaded: one PUT, or a multipart upload with the same parts.
- Server settings (all optional): `TimeStore:Replication:Enabled`, `Concurrency` (4), `PollSeconds` (5),
  `BatchDelayMilliseconds` (200), `MaxBackoffSeconds` (900), `RequestTimeoutSeconds` (900); see
  [configuration](../configuration.md).

## From a script

The same actions are in the admin API (signed-in session, `admin` role to change): `GET`, `PUT` and `DELETE
/api/v1/buckets/{name}/replication`, and `POST …/replication/test`, `…/retry`, `…/resync` (docs/api/admin.md,
"Replication").
