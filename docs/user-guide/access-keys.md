# Access keys

**Access Keys** lists the keys S3 clients use, under a card with the S3 endpoint and an AWS CLI command to copy. The root key from the server configuration is not
listed; it always works and cannot be changed here.

| Column | Meaning |
|---|---|
| Access key | The key id (`TS…`), what a client sends as `AWS_ACCESS_KEY_ID`. "You" marks the key you are signed in with |
| Description | Who or what uses the key |
| S3 access | **All buckets**: every bucket, unless a bucket policy denies it. **Bucket policies only**: only what a bucket policy allows (a key to share one bucket with someone); it cannot list or create buckets |
| Role | **S3 only**: S3 API only. **Viewer**: S3, plus this UI read-only. **Admin**: S3, plus this UI with every action. **Backup**: for backup programs only: the backup API with signed requests, no sign-in to this UI |
| Status | **Active**, or **Disabled** (S3 requests fail with `InvalidAccessKeyId`, sign-in is refused) |

Viewers see the list only.

## Creating a key

1. **Generate Access Key**, give it a description (optional) and choose its S3 access and its role.
2. The next screen shows the **access key** and the **secret key**, and the two `export` lines for a shell. Copy
   them now: the secret is stored encrypted and is never shown again. A lost secret means a new key.
3. **I have saved the secret** closes the dialog; the secret is gone from the page.

## Changing a key

The **⋮** menu at the end of a key:

- **Admin UI access**: pick S3 only, Viewer, Admin or Backup (for backup programs: [Backups](backups.md)). The change
  ends the key's admin sessions at once.
- **S3 access**: All buckets or Bucket policies only. S3 clients see the change from their next request.
- **Disable** / **Enable**: a disabled key keeps its id and secret and works again once enabled.
- **Delete**: type the key id to confirm. Clients using it fail from their next request; this cannot be undone.

Changing or disabling the key you are signed in with asks first and then signs you out; sign in with another admin
key or the root key.
