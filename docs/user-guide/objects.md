# Browsing objects

**Buckets** shows the buckets as cards, a list or a table (the switch on the right; **Settings** keeps your choice),
sorted by name, size or object count; the bar on a card is its share of all stored bytes. A bucket's card opens its
objects. Folders are parts of object keys separated by `/` (`photos/2026/cat.jpg` sits in folder `photos/2026/`); the
bar above the list shows where you are, and each part opens that level.

**+ Create Bucket** (admins) asks for a name of 3–63 characters: lowercase letters, digits, hyphens and dots, starting and
ending with a letter or digit. What you type is fixed as you go: letters become lowercase, accents are removed
(`Ảnh` → `anh`), spaces and underscores become hyphens, and other characters are dropped. A name that still breaks a
rule (too short, ending with a hyphen, looking like an IP address) is explained under the field.

## Looking at objects

- Objects show as a list (default), a grid with image thumbnails, or a table: the switch at the right of the bar.
- Click a folder to open it. Entries are listed in name order, 200 at a time: scroll and the next ones load by
  themselves; the line under the list says how many are loaded. Folders sort by name with the files, so in a large
  folder a sub-folder named `sub/` appears after `file-…` (use the filter to jump to it). Coming back to a folder
  returns to where you were in it.
- **Names starting with…** filters the current folder by the start of the name (case-sensitive), e.g. `2024-` or
  `invoice`. It asks the server, so it works the same with a hundred files or millions. Opening another folder clears
  it.
- Click an object for its preview window: the preview on the left; on the right its details (size, type, version,
  last change, content type, ETag, checksum, parts, headers and user metadata), its links, **Download File** and
  **Delete Object**. The download icon in each row downloads directly.
- **Preview**: images (up to 25 MB), video and audio play in the panel; text files (plain text, JSON, YAML, XML, CSV,
  …) show their first 64 KB with line numbers and **Copy**; CSV files show as a table (**Source** for the raw text). HTML, PDF and other types are download only: they are never opened as a page here.
- Downloads always save the file; the browser never opens it as a page.

## Links and sharing

The preview window shows how the object is reached outside this UI:

- **S3 URL**: the object's address on the S3 port, e.g. `http://host:9000/bucket/key`. **Private** means it needs a
  signature (an S3 client with a key); **Public** means the bucket policy lets anyone open it. **Bucket policy** opens
  the policy editor to change that.
- **Share link** (admins): pick 1 hour, 1 day or 7 days and **Create link**. Anyone with the link can download this
  one object until it expires, even from a private bucket. The link is signed with the key you are signed in with:
  disabling or deleting that key stops every link it made. Links are not stored anywhere; copy it when it is shown.
- Behind a proxy or on another host name, set `TimeStore:S3:PublicUrl` so the links point to the right address.

## Uploading (admins)

- **Upload** picks files, **⋯ → Upload folder** picks a folder and keeps its structure; you can also drop files
  anywhere on the page, or use **Upload Files** in the sidebar. Uploads go into the folder you are in.
- Files over 64 MiB are sent in parts (three at a time); the panel at the bottom right shows progress on every page.
  **×** cancels a file; nothing of a cancelled file stays on the server.
- An object with the same key is replaced.

## Folders and deleting (admins)

- **New folder** creates an empty folder (a zero-byte object named `folder/`, as S3 consoles do), so it stays
  visible while empty.
- The bin icon of a row deletes that object (after a confirmation). To delete several, tick their boxes, then
  **Delete**. In the table the boxes are always shown; in the list and the grid a box appears when you point at a row
  (always on a touch screen, and on every row once something is selected). Deleting a folder deletes every object under it: type
  `permanently delete` to confirm. This cannot be undone.

Viewers can browse, open details and download only.

## Versions

The line under a bucket's name shows **Versioning Off**, **On** or **Suspended**. Admins use **⋯ → Enable
versioning**, and **Suspend versioning** later (after a confirmation); a bucket never goes back to Off.

- With versioning enabled, every upload keeps the previous content as an older version, and deleting an object adds a
  **delete marker**: the object disappears from the list but its versions stay.
- **⋯ → Show versions** lists every version and delete marker, newest first, with the end of its version id (the full id
  in the tooltip). Older versions can be downloaded, inspected, **restored** (↶: copied over the object as its newest
  version) or deleted for good (type `permanently delete`). Deleting a delete marker brings the object back.
- While suspended, uploads replace the object's single `null` version; versions kept before stay.

## Lifecycle rules

**⋯ → Lifecycle rules** on a bucket's page lists its rules. Each rule applies to keys starting with a prefix (or to every key)
and can:

- delete objects a number of days after they were written (in a versioned bucket: hide them behind a delete marker),
- delete old versions a number of days after they were replaced, keeping the newest few if you ask,
- remove delete markers that have no versions left,
- abort uploads that were never finished.

Days end at midnight UTC: an object written today with "after 1 day" goes after the second midnight. The server
applies the rules every hour; **Run now** applies them at once. Admins add, edit and delete rules; a disabled rule is
kept but does nothing.

## Making a bucket or folder public (admins)

- **⋯ → Make bucket public** on the bucket's page: anyone who can reach the S3 port can read every object in the bucket
  without signing in (`http://host:9000/bucket/key`). It asks first. **Make bucket private** undoes it.
- The globe button of a folder does the same for that folder and everything under it. A **Public**
  badge marks public folders; a folder inside a public folder (or in a public bucket) shows the badge too, and its
  button is disabled: make the parent private instead.
- Only reading becomes public: nobody can list the bucket or write to it without a key.
- These buttons edit one statement of the bucket policy (`TimeStorePublicRead`); anything else you wrote in the policy
  stays as it is.

## Bucket policy

**Policy** on a bucket's page shows the bucket's policy: a JSON document (the Amazon S3 / IAM format) that says who
may do what. **Add policy** starts from an example that lets anyone read keys under `public/`; edit it and **Save**.
The server checks the document and shows what is wrong; nothing changes until it is valid. **Public** next to the
title means a statement lets anyone, without signing, do something.

- The **root key** ignores policies, so a wrong policy can always be fixed.
- Keys with **All buckets** S3 access can do everything unless a `Deny` statement names them (or `*`).
- Keys with **Bucket policies only** S3 access, and unsigned requests, can only do what an `Allow` statement grants.
- Principals are access key ids (`"AWS": "TS…"`) or `"*"`; resources are `arn:aws:s3:::bucket` (bucket actions such
  as `s3:ListBucket`) and `arn:aws:s3:::bucket/prefix*` (object actions such as `s3:GetObject`).

**Remove policy** deletes it; the bucket is private again.
