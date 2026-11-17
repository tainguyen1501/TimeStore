# Bucket lifecycle (step 6c)

Sources: the Amazon S3 user guide (Managing your storage lifecycle, Lifecycle configuration elements, Expiring
objects, How S3 calculates when an object expires) and API reference (Put/Get/DeleteBucketLifecycleConfiguration),
ceph s3-tests (`test_lifecycle_*`: the configuration tests run here; the expiration tests need RGW's debug clock),
and step 6a (`object_versions.superseded_at` was added for this step).

## 1. The model (Amazon S3)

- A bucket has at most one configuration of up to 1 000 rules. A rule has an `ID` (≤ 255 characters, unique; generated
  when absent), a `Status` (`Enabled` / `Disabled`, case-sensitive: anything else is `MalformedXML`), a filter and
  at least one action.
- **Filter**: `Prefix`, `Tag`, `ObjectSizeGreaterThan`, `ObjectSizeLessThan`, or `And` of several; an empty filter
  matches everything; the older form puts `Prefix` directly in the rule.
- **Actions**:
  - `Expiration` with `Days` (> 0) or `Date` (midnight UTC): the current version expires. Unversioned bucket: the
    object is deleted. Versioned bucket: a delete marker is added (the data stays as a noncurrent version).
  - `Expiration` with `ExpiredObjectDeleteMarker`: a delete marker that is the only version left of its key is removed.
    It cannot be combined with `Days` or `Date`.
  - `NoncurrentVersionExpiration`: `NoncurrentDays` after a version became noncurrent it is removed for good;
    `NewerNoncurrentVersions` keeps that many of the newest noncurrent versions regardless.
  - `AbortIncompleteMultipartUpload`: `DaysAfterInitiation` after it started, an upload is aborted.
  - `Transition` / `NoncurrentVersionTransition`: move to other storage classes.
- **Timing**: Amazon S3 rounds to the next midnight UTC: an object created at 10:00 on day D with `Days: 1` is
  eligible from midnight starting day D+2. Work runs asynchronously, typically once a day; objects are guaranteed to
  stop being billed from that moment, not to be gone.
- `GetBucketLifecycleConfiguration` without a configuration: 404 `NoSuchLifecycleConfiguration`;
  `DeleteBucketLifecycle` answers 204 either way. A PUT replaces the whole configuration and needs `Content-MD5` or a
  checksum.

## 2. Design for TimeStore

| Topic | Decision |
|---|---|
| Storage | `lifecycle_rules` (migration v8): one row per rule in configuration order, columns for every supported field; cascades with the bucket |
| Supported | Filter: prefix, size greater/less than (alone or in `And`). Actions: expiration (days, date, expired delete markers), noncurrent expiration (days, newer versions kept), abort incomplete uploads |
| Eligibility | `created ≤ floor_midnight(now) − days` (equivalent to S3's "creation + days rounded up to midnight ≤ now"); noncurrent versions count from `superseded_at`; `Date` rules from that instant |
| Execution | `ILifecycleRunner` walks each bucket with enabled rules through `ListObjectVersions` (one pass covers current objects, noncurrent versions and delete markers), deletes in batches of up to 1 000 through `DeleteObjectVersionsAsync` (so a versioned bucket gets delete markers exactly as a client DELETE would), and aborts uploads; a hosted worker runs it every `TimeStore:Lifecycle:IntervalMinutes` (60) |
| Races | In an unversioned bucket an expiration names the exact version it saw, so an object rewritten meanwhile is never deleted by an old decision |
| Abandoned uploads | `TimeStore:Multipart:AbandonAfterDays` stays as the node-wide default; lifecycle abort rules are per bucket and may be shorter |
| Admin | `GET/PUT/DELETE /api/v1/buckets/{name}/lifecycle` (JSON), `POST /api/v1/maintenance/lifecycle` (run now, returns counts); a rules editor on the bucket page |

**Tag filters and transitions are refused**: TimeStore has no object tags and one storage
class; accepting such a rule would store a promise nothing keeps (a tag filter would never match, a transition would
never move anything). Tag filters answer 501 `NotImplemented`; transitions answer 400 `InvalidStorageClass` (what
Amazon S3 answers for a class it does not have, and what ceph `test_lifecycle_transition_set_invalid_date` expects
as a 400). Dates are validated first, so a malformed date is still a 400 `InvalidArgument`.

**An admin "Run lifecycle now" action** (`POST /api/v1/maintenance/lifecycle`) next to the
hourly worker: it makes the effect of a new rule visible at once and is how end-to-end tests drive the runner.

## 3. Notes from step 6c

- Domain `LifecycleRule` / `LifecycleFilter` / `LifecycleTime.DueIfStartedBy`; store `Get/Set/DeleteLifecycleAsync`,
  `ListBucketsWithLifecycleAsync` (migration v8, contract tests); `VersionSummary.SupersededAt` from the version
  listing (contract test).
- `LifecycleService` (configuration; generates missing ids, validates with `LifecycleValidation`) and
  `LifecycleRunner` (execution) in Infra; `LifecycleWorker` (hosted, `TimeStore:Lifecycle:Enabled`,
  `IntervalMinutes`), admin `GET/PUT/DELETE /api/v1/buckets/{name}/lifecycle` and `POST /api/v1/maintenance/lifecycle`.
- S3: `?lifecycle` left the "unsupported" list; Get/Put/DeleteBucketLifecycle (PUT needs Content-MD5 or a checksum),
  a small XML tree reader for the nested rule document, the writer returns `Filter` (with `And` when sizes are set).
- Runner tests run on a real SQLite store with a manual clock (the unit test project now references
  `Metadata.Sqlite`): midnight rounding, date rules, size filters, versioned expiration (marker, then the noncurrent
  data after `NoncurrentDays`), kept newer versions, lone markers, aborted uploads, disabled rules.
- End to end: configuration round trip over S3, generated/too long/duplicate ids, refused tags and transitions,
  admin "run now" with a due date rule and a lone delete marker; browser run of the rules panel.
- AWS SDK v4 reports `NoSuchLifecycleConfiguration` as an empty configuration (no exception); the server answers 404
  as Amazon S3 does (checked with a signed curl request, and by ceph `test_lifecycle_delete`).
- ceph s3-tests: the `lifecycle` marker (configuration tests) is no longer excluded; expiration tests stay out (they
  need RGW's debug clock).
