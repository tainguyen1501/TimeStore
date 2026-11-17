# Admin UI (phase 7)

Research before code for the React admin UI. Step 7a (sign-in) first: since the admin sign-in step, every `/api/v1`
endpoint but `auth/login` answers 401 without a session, so the existing Buckets and Overview pages cannot load.
Server side (sessions, roles, CSRF, throttling) is decided in `docs/research/admin-auth.md` and documented in
`docs/api/admin.md`; this note covers only the browser side.

Sources: OWASP guidance (Authentication Cheat Sheet: generic errors, password managers;
Unvalidated Redirects Cheat Sheet).

## 3. Guidance

- Generic error for bad credentials (the server already answers one 401 for every cause).
- Fields work with password managers: `autocomplete="username"` for the access key and `current-password` for the
  secret; secrets never go into the URL, local storage or logs. Nothing is stored in the browser besides the
  `HttpOnly` cookie the server sets.
- Validate `redirect` before following it.

## 4. Proposal for TimeStore

| Topic | Proposal | Why |
|---|---|---|
| Fields | **Access key** + **Secret key** (as an S3 client asks for them), secret with show/hide | The credentials are S3 keys; same words as the rest of TimeStore |
| Layout | `AuthLayout`, `AuthError`, `SecretInput`, `SubmitButton` with TimeStore branding | One sign-in frame for every auth page |
| Guard | `_app.beforeLoad` on `auth/me` (no retry), redirect to `/login?redirect=…`; `safeRedirect` | No page renders without a session |
| Signed-in user on `/login` | Redirect to `redirect` or `/` | Nothing to do there |
| Session expired while open | Global 401 interceptor → clear cache → `/login?redirect=…`; no 401 toast | The user lands back where they were after signing in |
| Errors | 401 `AUTH_INVALID_CREDENTIALS`: one generic message; 429 `AUTH_TOO_MANY_ATTEMPTS`: "Too many attempts" with the wait from `Retry-After`, the button disabled until it passes | The throttle exists server side; a countdown stops pointless retries |
| No "keep me signed in" | Not offered | The server has one session lifetime (`Admin:SessionHours`); a checkbox that changes nothing would mislead |
| Who is signed in | User menu in the header: access key, role, session end, "Sign out" | The role decides what the user may do |
| Viewer role | See decision 1 | |
| Out of 7a | Access keys page, object browser, metrics, serving the build from the Server, generated OpenAPI types | Later steps of phase 7 |

### Steps of phase 7

| Step | Content |
|---|---|
| 7a | Sign-in page, guard, session expiry, user menu, viewer read-only UI |
| 7b | OpenAPI document on the admin port, generated types (`pnpm gen:api`) replacing `@types/models/`, serve the build from the Server (static files + SPA fallback) |
| 7c | Access keys page (list, create with the one-time secret, role, disable/enable, delete) |
| 7d | Object browser (prefix navigation, upload, download, delete) |
| 7e | Metrics |

## 5. Decisions (2026-11-16)

1. **Viewers do not see write actions**: the UI leaves out create, delete and other write
   buttons for the `viewer` role, and the header shows a "Read-only" badge so the missing buttons have a reason. The
   server still refuses every write (403); the UI only avoids offering actions that would fail.
2. **OpenAPI and generated types right after sign-in** (7b): the access keys and object pages are written against
   generated types from the start instead of hand-written models replaced later.
3. The rest of section 4 as proposed (access key + secret fields, auth components, guard on `auth/me`,
   `safeRedirect`, global 401 handling, generic 401 message, 429 countdown from `Retry-After`, no "keep me signed in").

## 6. Notes from step 7a

- Routes: `/login` (outside the shell) and the pathless `_app` layout, whose `beforeLoad` loads `auth/me` through
  the query cache (no retry) and redirects to `/login?redirect=<href>` on 401. `/login` sends a signed-in user to
  `safeRedirect(redirect)`.
- `main.tsx`: a 401 from any non-auth call (session expired, key disabled or demoted, signed out elsewhere) clears
  the query cache and goes to `/login?redirect=…`; action toasts skip 401.
- Viewer UI: `useCanWrite()` (`role === "admin"`) gates write actions (Buckets: Create, Delete; the empty state says
  an admin can create buckets); the header shows "Read-only" for `viewer`. Every later page uses the same hook.
- Sign-in page: generic server message on 401, secret cleared and focused for another try; on 429 the
  `Retry-After` seconds count down in the error box and **Sign in** stays disabled until they pass. Fields use
  `autocomplete="username"` / `current-password` so password managers work; nothing is stored in the browser.
- Components: `AuthLayout`/`AuthError`, `SecretInput` (a password field with secret-key labels), `SubmitButton` (with a `disabled` prop for the throttle), `UserMenu` (access key, role, session end).
- Verified in a browser (headless Chromium against `pnpm dev` + the Server): redirect with `redirect`, field errors,
  wrong secret, admin sign-in back to `/buckets`, user menu, sign out, `/buckets` refused after sign out, viewer badge
  without Create/Delete, open redirect refused (`//evil.example` → `/`), cleared cookie → sign-in, 429 countdown with
  the button disabled; no console errors besides the expected 401s.

## 7. Step 7b: OpenAPI, generated types, UI served by the Server

### .NET 11 building blocks (checked)

- `Microsoft.AspNetCore.OpenApi` 11.0.0 builds the document at runtime from endpoint metadata and supports Native
  AOT when JSON uses source generation (the schemas come from the configured `JsonTypeInfo` chain, here
  `AdminJsonContext`). `Results<Ok<T>, JsonHttpResult<ErrorResponse>>` documents the 200 only: `JsonHttpResult`
  carries no static status, so error responses must be declared (`Produces<ErrorResponse>(status)`).
- `Microsoft.Extensions.FileProviders.Embedded` 11.0.0: `ManifestEmbeddedFileProvider` keeps directory paths of
  embedded files (the plain `EmbeddedFileProvider` flattens them); it reads resources with
  `Assembly.GetManifestResourceStream`, which Native AOT keeps.
- `StaticFileMiddleware` gives `ETag`/`Last-Modified`, conditional requests and ranges for any `IFileProvider`.
  `MapFallbackToFile` would also answer unknown `/api/v1/...` paths with the page (its `{*path:nonfile}` pattern
  matches them), and the slim builder has no regex route constraints, so the fallback is a small explicit middleware.
- `ResponseCompression` (gzip/Brotli) is AOT-safe and can be limited to MIME types.

### Decisions (2026-11-16)

1. **The OpenAPI document is served only when `TimeStore:Admin:OpenApi` is true** (default
   false; true in `appsettings.Development.json`), at `/openapi/v1.json` on the admin port. Production servers do not
   publish an inventory of their admin API. A committed snapshot, `docs/api/openapi.v1.json`, is the source for the
   frontend types (`pnpm gen:api` reads the file, no running server needed) and is pinned by an end-to-end test that
   compares the served document with it (`TIMESTORE_UPDATE_OPENAPI=1` rewrites the snapshot).
2. Error responses are declared per endpoint (`ErrorResponse` with the statuses `docs/api/admin.md` lists), so the
   generated client knows the error body; `@types/models/` is deleted and every type comes from `api.gen.ts`.
3. **The UI is embedded in the Server binary**: the build embeds
   `frontend/dist` when it exists; `TimeStore:Admin:UiPath` serves a directory instead (custom UI builds, tests).
   Without either, the admin port serves the API only and `/` answers 404 with a hint. One binary to ship, and the
   UI always matches the API it was built with.
4. Serving: admin port only (the S3 branch answers everything on the S3 port first); `GET`/`HEAD` outside `/api` and
   `/health`. A path that names a file is served or 404; any other path gets `index.html` (client routes survive a
   reload). Hashed files under `/assets/` are `Cache-Control: public, max-age=31536000, immutable`; `index.html` is
   `no-cache`. UI responses carry `Content-Security-Policy` (`default-src 'self'`, inline styles allowed for the
   component library, `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff` and
   `Referrer-Policy: no-referrer`.
5. **Compression only for UI text files** (HTML, JS, CSS, SVG, JSON manifests), never for
   `/api` responses: compressing responses that carry secrets (a created access key) next to attacker-influenced
   input is the BREACH pattern, and API responses are small.
6. The release image builds the frontend in its own stage (Node 22, pnpm from `packageManager`) and the Server
   build embeds it; `deploy/Dockerfile` stays the one way to build a release.

### Notes from step 7b

- `AdminUi` (Server): `MapWhen` branch for UI requests: response compression, static files, then the SPA fallback
  (`index.html` for extension-less paths), static files again. The file provider is
  `ManifestEmbeddedFileProvider(ui/)` dated with the binary's time stamp (Native AOT has no assembly location), or a
  `PhysicalFileProvider` for `Admin:UiPath`; a build without `frontend/dist` finds no `ui/` in the manifest and
  answers 404 with a hint.
- MSBuild pitfall: the embedded files' `Link` must use `/`; with `\` the manifest recorded one directory literally
  named `ui\assets` on macOS and nothing was found.
- `AdminOpenApi`: OpenAPI 3.1 (`openapi-typescript` 7 reads 3.1; the default in .NET 11 is 3.2), no `servers` (the
  URL would carry the port and break the snapshot), title "TimeStore admin API". Every endpoint declares its error
  statuses with `ErrorResponse` (`Errors(...)` in `AdminApi`).
- The Content-Security-Policy found two real problems in the built UI during the browser run against the AOT image:
  the inline theme script (moved to `public/theme-init.js`, still blocking so no flash) and the small fonts Vite
  inlines as `data:` URIs (`font-src 'self' data:`). The rule "no inline script" is in `.claude/rules/frontend.md`.
- Verified: unit/e2e suite (685 tests) and the server e2e tests against the macOS AOT binary (175); AOT publish
  without IL warnings (binary 20.6 MB with the UI); the Linux image serves the UI and the full sign-in flow passes in
  headless Chromium with no console errors.

## 8. Step 7c: access keys page

### Decisions (2026-11-16)

1. The secret stays **shown once** (decided in phase 2: stored encrypted, never returned again). The create dialog
   switches to a result view with the access key and secret, copy buttons, an S3 client snippet and the warning that
   the secret cannot be shown again; closing it drops the secret from memory (the mutation is reset, nothing is
   cached).
2. **No client-chosen secrets.** The server generates every secret (256-bit, as today);
   typing a secret invites weak ones and adds a second path to validate.
3. Row actions: role (`none`/`viewer`/`admin`, with what each allows), disable/enable, delete (typed confirmation of
   the access key id). The page says that changing the role or disabling a key ends its admin sessions at once.
4. **An admin may change, disable or delete the key they are signed in with**, after a
   confirmation that says so; the next call then answers 401 and the UI returns to sign-in. Refusing it would need a
   server rule the API does not have, and the root key always remains as the way back in.
5. Viewers see the list (without secrets, as for admins) and no actions.
6. `SigningKeyCache` (SigV4 signing keys per access key, date and region) becomes a bounded LRU: today it is cleared
   whole at 4 096 entries, so one burst of new keys evicts every hot key at once. Least recently used entries go
   first, in batches, so eviction stays rare.

### Notes from step 7c

- Page `/access-keys` (menu link instead of "Soon"): table of key, description, role, status, created; a "You" badge
  on the signed-in key. Row menu: role (radio items with what each allows; `closeOnClick`, Base UI keeps radio menus
  open otherwise), disable/enable, delete (typed key id).
- The create dialog swaps to a result view: access key, secret and an `export` snippet, both masked until shown, copy
  buttons, a warning; "I have saved the secret" resets the mutation so the secret leaves the page. The response is
  never put in the query cache.
- Own key: role change and disable go through a confirmation that says the session ends; afterwards the 401 sends
  the user to sign-in (verified).
- `SigningKeyCache`: entries carry a last-use tick; above capacity the least recently used eighth is removed under a
  lock (readers never wait; a replaced entry is not removed by a stale snapshot: `TryRemove(KeyValuePair)`). Tests:
  hot key survives 100 cold inserts at capacity 16; parallel use stays bounded and correct.
- Verified in headless Chromium against the Server serving `frontend/dist` (`Admin:UiPath`): create with each role,
  secret signs in, role change, disable/enable, own-key disable → sign-in, viewer sees no actions, typed delete; the
  result dialog no longer overflows with the long snippet line (`min-w-0`).

## 9. Step 7d: object browser

### What S3 consoles do (AWS)

Folders are a view over the `/` delimiter; "Create folder" writes a zero-byte object named `folder/`; deleting a
folder deletes every object under the prefix after the user types a confirmation; uploads above a size use multipart
upload with parts uploaded in parallel and a progress bar; downloads are attachments.

### Options for the transport

| Option | For | Against |
|---|---|---|
| Admin endpoints (same origin) | Session cookie, roles and CSRF checks already guard them; no CORS; works wherever the admin UI is reachable | Bytes pass through the admin port (the same process anyway) |
| Presigned S3 URLs from the browser | Uses the S3 path as-is | The browser must reach the S3 port (often not exposed where the admin UI is), the S3 port needs CORS, and the UI would sign with a key the session does not have |

### Decisions (2026-11-16)

1. **Admin endpoints on the admin port**, calling the same services as S3
   (`IObjectService`, `IUploadService`) through a new `IObjectBrowserService` that maps to Contracts. Keys travel in
   the query string (`?key=`), never in the route, so any key (slashes, spaces, `%`) round-trips.
2. **Uploads send the raw file as the request body** (not `multipart/form-data`): it streams straight into the blob
   store with no temporary copy. Files up to 64 MiB are one request; larger files use a multipart upload through the
   admin API: parts of `max(16 MiB, size / 10 000)`, three in flight, per-file progress, abort on failure or cancel.
   Request body limits are set per endpoint (5 GiB, the S3 single-request maximum).
3. **Downloads are always attachments** with `Content-Security-Policy: sandbox` and `X-Content-Type-Options: nosniff`:
   an uploaded HTML or SVG file must never run as a page on the admin origin (stored XSS against admins).
4. **"New folder" writes a zero-byte `name/` object** (as the AWS console), so an empty folder
   stays visible.
5. **Deleting a folder deletes every object under its prefix**, after the user types
   `permanently delete` (as the AWS console; one phrase whatever the selection); the server expands the prefix page by
   page (1 000 keys per metadata transaction).
6. Listing: delimiter `/`, 200 entries per page, "Load more" continues after the last key. Details panel: size, ETag,
   content type, last modified, checksum, system headers and user metadata.
7. Responses carry a nullable `versionId` (always null until step 6a adds versioning), so the browser can show
   versions without a contract change.
8. Viewers browse, open details and download; upload, new folder and delete are admin only.

### Notes from step 7d

- `IObjectBrowserService` (Application) / `ObjectBrowserService` (Infra) delegate to `IObjectService` and
  `IUploadService`; endpoints in `AdminObjectApi`. Kestrel's 30 MB default body limit is raised per request to 5 GiB
  for the two upload endpoints only (`IHttpMaxRequestBodySizeFeature`).
- JSON numbers are strict now (`JsonNumberHandling.Strict` on the HTTP options): with the Web default (read numbers
  from strings) the OpenAPI schema said "integer or string" and the generated TypeScript `number | string`.
- UI: `/buckets/$bucket?prefix=` with breadcrumbs, folders first, selection (admins), "Load more"; details sheet with
  download; new folder dialog; uploads from a file picker, a folder picker (keeps relative paths) or drag and drop,
  queued in a store (`store/uploads.ts`, two files at a time) and shown in a panel on every page; cancel aborts the
  request and the multipart upload.
- Browser run (14 checks) against the Server: folder, 14 B and 70 MiB uploads (5 parts of 16 MiB), details, a
  byte-identical download, upload into a folder, breadcrumb, a cancelled 300 MiB upload leaves no object and no pending
  upload (S3 ListMultipartUploads), typed folder delete, viewer read-only. Pitfall for such scripts: while a modal is
  open Base UI hides the page from the accessibility tree, so role queries for rows find nothing; wait for the dialog
  to close first.
- Tests: `ObjectBrowserApiTests` (12): round trip with S3, sandboxed downloads, folder markers, paging, multipart,
  abort, a 40 MiB single upload, recursive folder delete over 1 005 objects, prefix validation, unknown bucket, viewer.

## 10. Step 7e: metrics

### What Amazon S3 reports

Storage metrics per bucket, daily: `BucketSizeBytes`, `NumberOfObjects`. Request metrics (opt-in, per minute):
`AllRequests`, `GetRequests`, `PutRequests`, `DeleteRequests`, `ListRequests`, `4xxErrors`, `5xxErrors`,
`BytesDownloaded`, `BytesUploaded`, `FirstByteLatency`, `TotalRequestLatency`.

### Decisions (2026-11-16)

1. **Per-bucket object count and bytes are kept by SQLite triggers** on `objects`
   (insert, size update, delete) into `bucket_usage`, in the same transaction as the write (migration v6, backfilled
   once). Exact at every moment, no table scan on the admin page, no change to the 17 write paths. Incomplete
   multipart uploads (count and bytes) are summed on request (the uploads tables are small). Versioning (6a) revisits
   the triggers.
2. **Request metrics are counted in process since start** on the S3 port: requests by class (read, write, list,
   delete, other, following S3's request metrics), client and server errors, bytes in and out, latency in a fixed
   histogram (1 ms … 10 s). A sampler stores one point every 10 s in a ring buffer of 90 (15 minutes): requests,
   errors, bytes, p50/p99 latency of the interval, storage totals. Nothing is persisted; a restart starts over.
3. **No Prometheus/OpenTelemetry export in this step**; the counters sit behind one class so
   an exporter can be added later without touching the hot path again.
4. Disk: total and free bytes of the volume holding the data directory.
5. `GET /api/v1/metrics` (viewers too); the Overview shows the headline cards, and a Metrics page shows charts of the
   samples and the per-bucket table. The UI polls every 10 s.

### Notes from step 7e

- Migration v6 (`bucket_usage` + four triggers, backfill), store methods `ListBucketUsageAsync` /
  `GetUploadUsageAsync` with contract tests (every write path, multipart before and after completion, bucket delete)
  and a migration test (backfill from v5 data).
- `S3RequestMetrics` (Server): interlocked counters per class, errors, bytes, a 14-bucket latency histogram;
  `Classify` is an exhaustive switch over the closed `S3Operation` hierarchy (a new operation breaks the build until
  it is classified). The middleware is the first of the S3 branch; an exception counts as a 5xx.
- `MetricsSampler` (hosted): a point at start, then every 10 s; 90 points. `GET /api/v1/metrics` assembles storage
  (live), totals and samples.
- UI: Overview cards (stored, disk with a usage bar, request rate, p99 latency, each with a sparkline), Metrics page (traffic cards, buckets by size, totals since start); polled every 10 s; "Metrics"
  is a menu link now.
- Verified: S3 traffic from curl (SigV4) shows exact numbers (43 writes, 31 reads, 1 client error, 3 000 560 bytes
  in, 420 out, 41 objects, sizes per bucket); browser run with no console errors. Hot path: no measurable cost
  (`docs/performance.md`).

## 11. Step 7f: object preview and links

Asked for after phase 6: the details panel must show the object and how to reach it from outside. Sources: the
Amazon S3 guide (sharing objects with presigned URLs, 7-day limit), the S3 console (object overview: "Object URL",
"Share with a presigned URL"), and our own SigV4 verifier.

| # | Decision | Why |
|---|---|---|
| 1 | Previews only for images (≤ 25 MB, `<img>`), video and audio (media elements) and text (first 64 KB in `<pre>`); HTML, PDF and the rest stay download only | Nothing uploaded may run as a page on the admin origin (stored XSS). An `<img>` never runs SVG scripts; the content endpoint keeps `attachment` + `sandbox` + `nosniff`. PDF needs a plugin the UI's CSP (`object-src 'none'`) blocks |
| 2 | Share links are presigned GET URLs signed with the **signed-in key**, 1 s – 7 days (UI: 1 hour, 1 day, 7 days), admins only | Revocable: disabling or deleting the key kills its links (a link signed with the root key dies only with a new root secret). Same format and limits as Amazon S3; nothing new to store |
| 3 | Links are returned once (`no-store`), kept only in the mutation's state, never stored or logged | A share link is a bearer credential |
| 4 | `TimeStore:S3:PublicUrl` (optional) names the S3 endpoint for links; default: the admin request's host with `S3Port` | The server cannot know the outside address behind a proxy; SigV4 signs the host |
| 5 | "Public" = the bucket policy lets an anonymous caller `s3:GetObject` this key with an empty condition context | A conditional grant (source IP, TLS) is not shown as public |

Notes from step 7f:

- `SigV4Presigner` (Protocol) reproduces the Amazon S3 documentation's presigned URL signature exactly (unit test)
  and its URLs pass our verifier for odd keys, versions and non-default ports.
- Admin API: `GET …/objects/access`, `POST …/objects/share` (`OBJECT_INVALID_SHARE_EXPIRY`). 8 end-to-end tests:
  URL and public state before/after a policy, link reads a private object without credentials and nothing else,
  versioned link, link revoked by disabling its key, expiry bounds, viewers refused, missing object.
- Browser run: image preview, text preview, S3 URL private (403 without signing), 1-day share link opens without
  credentials, "Bucket policy" link → public policy → Public badge and the S3 URL opens.

## 12. Step 7g: make a bucket or folder public

Asked for after 7f. Sources: the Amazon S3 guide (granting read-only permission to anonymous users with a bucket
policy, prefix resources, `s3:prefix` for listings), the S3 console's "Block public access" flow (a confirmation
before anything becomes public).

| # | Decision | Why |
|---|---|---|
| 1 | "Make public" edits one statement owned by TimeStore (`Sid: TimeStorePublicRead`, `Allow` `*` `s3:GetObject` on `arn:aws:s3:::bucket/<prefix>*`); the server edits the JSON, other statements are kept as written; no statement left → the policy is removed | The bucket policy stays the one source of truth (the S3 API and the editor see it); the toggle cannot damage a policy someone wrote |
| 2 | Read only. Listing and writing stay private | The common case (images, downloads, static files); public listing or writes are rare and riskier; they remain possible in the policy editor |
| 3 | Prefixes are the bucket (`""`) or folders ending in `/`, without `*` or `?` | IAM has no escape for wildcards in resources: a folder named `a*` would make more public than shown |
| 4 | The edit is a compare-and-swap on the stored document (`ReplaceBucketPolicyAsync`), retried up to 5 times, else 409 `POLICY_CONFLICT` | Two admins (or an S3 `PutBucketPolicy`) at the same time never overwrite each other; no in-process lock (multi-node rule 4) |
| 5 | Folders inside a public bucket or folder show "Public" too, with the toggle disabled | Making a child private cannot work while its parent's statement allows it |
| 6 | Making something public asks first; making it private does not | Only the first exposes data |

Notes from step 7g:

- `PublicAccess` (Application) reads and edits the statement with `JsonNode` (AOT-safe calls only); the result is
  validated with `BucketPolicy.Parse` before it is stored. Store: `ReplaceBucketPolicyAsync` (contract test).
- Admin API: `GET/PUT /api/v1/buckets/{name}/public`. Tests: 13 unit (folder vs string prefix, bucket, kept
  statements, removal, idempotence, prefix validation), 5 end to end checked with unsigned requests (folder, bucket,
  private again, written policy kept, bad prefixes, unknown bucket).
- Browser run: folder public (only its objects readable), nested folder shown public through its parent with the
  toggle disabled, bucket public, listing still private, statement visible in the policy editor, everything private
  again. Found: the confirmation lost its target while closing (fixed: the path stays until it has closed).

## 13. Step 7h: large folders (virtualized list, name filter)

Asked for after 7g ("what if there are many files?"). Sources: the Amazon S3 console (1000 entries per page, prefix
search in the current folder, no totals), TanStack Virtual
(dynamic measurement, infinite loading, table spacer rows, scroll restoration). The server side needed no change:
listings were already cursor-paged with skip-scan over sub-folders (`docs/research/s3-objects.md`).

| # | Decision | Why |
|---|---|---|
| 1 | The object table is virtualized (TanStack Virtual): about 30 rows in the page whatever is loaded; row heights are measured (long names wrap) | "Load more" kept every row in the DOM; 2 600 loaded rows now mean 39 elements |
| 2 | The next page loads while scrolling (40 rows before the end), still 200 per request with the server's cursor | Same server cost as "Load more", without a button per page |
| 3 | The filter is a server-side **prefix** in the current folder (case-sensitive), not "contains" | Prefix is what S3 and the store index can answer at any size; "contains" would scan the folder |
| 4 | Fixed table layout; small screens hide "Last modified" | Column widths must not change as different rows scroll in; phones keep room for the name |
| 5 | Small screens virtualize against the page's scrolling area (with a scroll margin), not the table | The table does not scroll on its own there; otherwise every loaded row would render |
| 6 | Scroll position per folder kept for the tab (in memory) and restored when the folder is opened again | Going into a sub-folder and back should not lose the place in a large folder |

Notes from step 7h:

- New: `@tanstack/react-virtual` 3.14.26, `ObjectTable` (`components/objects/object-table.tsx`), `Table`
  `containerRef`, `useDebouncedValue`, `data-slot="app-scroll"` on the shell's content area. The listing keeps the
  previous rows while a new filter loads (`keepPreviousData`), but never shows another folder's rows.
- Browser run with 20 003 objects in one folder (seeded with the AWS CLI): first page 26 rows in the DOM; scrolling
  loaded 2 600 objects with 39 rows in the DOM; a jump to the middle renders in about 100 ms; a wrapped 198-character
  name gets a measured 77 px row; filter `file-1999` → 10 objects; no match → its own empty state; folder → parent →
  folder returns to the same scroll offset; filter cleared when another folder opens; phone viewport: 13–30 rows in
  the DOM while the page scrolls and pages load.
- Found while testing: an auto-layout table widened for a long name instead of wrapping it; spacer rows that span a
  hidden column made a phantom column on phones; the remembered position could not be restored before the first
  measurement because the spacer was empty (it now holds the full height until rows are measured).
- Not done: the versions view keeps "Load more" (it is opened for one folder's history, rarely huge); bulk folder
  operations as background jobs are in the roadmap backlog.

## 14. Step 7i: redesign

The user provided a design mock-up (read for its look and components) and asked for its components and colours on
TimeStore's features, UI only.

| # | Decision | Why |
|---|---|---|
| 1 | (user) Features the design has and TimeStore does not are hidden: favourites, shared files, recycle bin, rename, bucket colour and quota, cross-bucket search, sort by date or size | Nothing in the UI promises what the server does not do; listed in the roadmap backlog |
| 2 | (user) Panels without data are mapped: "Storage Details" = storage per bucket, "Traffic Analytic" = S3 requests by type, "Recent Files" = node status | Same places and shapes, real numbers |
| 3 | (user) The Guide page renders `docs/user-guide/*.md`, bundled at build time | One source, no API change |
| 4 | Tokens, not hex: the design's colours are theme tokens (light and dark); file kinds and bucket tints are one accent each (`--file-*`, `--palette-*`), the pastel tiles a `color-mix` of it (`tint`) | The project rule (no hard-coded colours) and one place to change a colour |
| 5 | A bucket's tint follows from its name (stable hash over five palettes); the ring uses the same colour | TimeStore stores no colour per bucket; cards and ring must agree |
| 6 | The card's bar is the bucket's share of all stored bytes (no quotas) | The design's "used of quota" has no data here |
| 7 | shadcn kept and restyled (rounded-xl controls, tonal fills, borderless dialogs, sheets and cards); `outline` became the white chip of tonal toolbars | The design's controls without a second component set |
| 8 | Fonts DM Sans, Plus Jakarta Sans, JetBrains Mono from `@fontsource` | The UI's CSP allows no font CDN |
| 9 | The shell fills the window edge to edge (after review: no 1540 px cap, no lavender margin or rounded corners); the right panel shows from `xl`, the navigation is a drawer below `lg` | Use the screen; phones keep one column |
| 10 | The toolbar keeps Policy, New Folder and Upload visible; Lifecycle rules, Make bucket public, versioning, Show versions, Upload folder and Refresh go in a **⋯** menu | The design's toolbar has three buttons; TimeStore has more bucket actions |
| 11 | Selection stays (bulk delete): a box over the file icon, shown on hover and while anything is selected | The design has no selection; TimeStore's multi-delete needs it |
| 12 | Object preview window as in the design (preview left, inspector right) with 7f's rules: images, video, audio, text (CSV as a table) only | Same safety as before (§11) |

Notes from step 7i:

- New: `AppShell` + `RightPanel`, `PageHeader`, `Toolbar`, `Segmented`, `FileIcon` / `FolderIcon`, `Donut`,
  `BarChart`, `ObjectBrowser` (list / grid / table, virtualized, grid lanes by width), `ObjectDialog`, bucket views,
  Guide (Markdown renderer for what the guide uses: headings, lists with wrapped lines, numbered lists, tables, code,
  links between pages) and Settings (appearance, default layouts, kept in the browser). Removed: the old header,
  theme toggle and account menu (now in the right panel), the details sheet and the table-only object list.
- Browser runs: every page in light and dark at 1600×1000 and on a phone (390×844), no console errors; the 20 003-
  object folder again (list view: 16–25 rows in the page, scroll loading, filter, phone scrolling). The scroll position
  on return now lands within one row (rows are estimated before they are measured).
- Found while testing: the list card stretched to the bottom with three files (now sized to its rows); the selection
  box left a gap before the icons (now over the icon); bucket colours differed between cards and ring.
