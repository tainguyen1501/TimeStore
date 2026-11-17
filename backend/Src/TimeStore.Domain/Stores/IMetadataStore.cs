using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Uploads;

namespace TimeStore.Domain.Stores;

/// <summary>
/// Metadata storage, shaped by S3 operations (not generic CRUD) so any backend can implement it: SQLite now,
/// sharded SQLite or Postgres later. Methods answer questions or commit atomically; they hold no business rules.
/// Every provider must pass <c>Tests/TimeStore.Metadata.ContractTests</c>.
/// </summary>
public interface IMetadataStore
{
    // --- Buckets ---------------------------------------------------------------------------------------------------

    /// <summary>The bucket, or null when it does not exist.</summary>
    ValueTask<Bucket?> FindBucketAsync(string name, CancellationToken ct);

    /// <summary>All buckets ordered by name (binary order).</summary>
    ValueTask<IReadOnlyList<Bucket>> ListBucketsAsync(CancellationToken ct);

    /// <summary>
    /// Atomic create: false when the name is taken. Services never check-then-insert; the store decides, so
    /// concurrent writers (other requests, other nodes later) cannot both win.
    /// </summary>
    ValueTask<bool> TryCreateBucketAsync(Bucket bucket, CancellationToken ct);

    /// <summary>
    /// Deletes the bucket when it holds no object, atomically with that check; uploads still in progress do not count
    /// (Amazon S3) and are aborted with it, their part blobs returned in the result.
    /// </summary>
    ValueTask<BucketDeletionResult> DeleteBucketIfEmptyAsync(string name, CancellationToken ct);

    // --- Versioning (6a) ------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets Enabled or Suspended; false when the bucket does not exist. Never back to unversioned (Amazon S3).
    /// </summary>
    ValueTask<bool> SetBucketVersioningAsync(string name, BucketVersioning versioning, CancellationToken ct);

    /// <summary>
    /// One version by id (an object or a delete marker, with whether it is the latest), or <see cref="VersionMissing"/>.
    /// </summary>
    ValueTask<VersionLookup> FindObjectVersionAsync(Guid bucketId, string key, VersionRef version, CancellationToken ct);

    /// <summary>The key's latest version when it is a delete marker (GET of a deleted key), else <see cref="VersionMissing"/>.</summary>
    ValueTask<VersionLookup> FindLatestDeleteMarkerAsync(Guid bucketId, string key, CancellationToken ct);

    /// <summary>
    /// Deletes in one transaction, each as Amazon S3 does for the bucket's state: without a version, the current
    /// object goes (unversioned) or a delete marker is added (Enabled: new id; Suspended: the null marker, replacing the
    /// null version); with a version, exactly that version or marker goes and the next newest becomes the latest.
    /// A version that does not exist is not an error (nothing happens).
    /// </summary>
    ValueTask<DeletionBatch> DeleteObjectVersionsAsync(Guid bucketId, IReadOnlyList<ObjectToDelete> objects, CancellationToken ct);

    /// <summary>
    /// Versions and delete markers in key order, newest first within a key, after (<paramref name="keyMarker"/>,
    /// <paramref name="versionMarker"/>) or after the whole <paramref name="keyMarker"/> without a version marker.
    /// Delimiter roll-up as in <see cref="ListObjectsAsync"/>.
    /// </summary>
    ValueTask<VersionPage> ListObjectVersionsAsync(
        Guid bucketId, string prefix, string? delimiter, string? keyMarker, VersionRef? versionMarker, int maxEntries,
        CancellationToken ct);

    // --- Lifecycle (6c) -------------------------------------------------------------------------------------------

    /// <summary>The bucket's lifecycle rules in configuration order, or null when it has no configuration.</summary>
    ValueTask<IReadOnlyList<Lifecycle.LifecycleRule>?> GetLifecycleAsync(Guid bucketId, CancellationToken ct);

    /// <summary>Replaces the whole configuration; false when the bucket does not exist.</summary>
    ValueTask<bool> SetLifecycleAsync(Guid bucketId, IReadOnlyList<Lifecycle.LifecycleRule> rules, CancellationToken ct);

    ValueTask DeleteLifecycleAsync(Guid bucketId, CancellationToken ct);

    /// <summary>Ids of the buckets that have at least one enabled rule (the lifecycle runner's work list).</summary>
    ValueTask<IReadOnlyList<Guid>> ListBucketsWithLifecycleAsync(CancellationToken ct);

    /// <summary>Changes the key's S3 scope (<see cref="AccessKeys.AccessKeyScopes"/>); false when it does not exist.</summary>
    ValueTask<bool> SetAccessKeyScopeAsync(string accessKeyId, string scope, CancellationToken ct);

    // --- Bucket policy (6d) ----------------------------------------------------------------------------------------

    /// <summary>The bucket's policy document as stored, or null.</summary>
    ValueTask<string?> GetBucketPolicyAsync(Guid bucketId, CancellationToken ct);

    /// <summary>Stores (replaces) the document; false when the bucket does not exist.</summary>
    ValueTask<bool> SetBucketPolicyAsync(Guid bucketId, string document, DateTime updatedAt, CancellationToken ct);

    ValueTask DeleteBucketPolicyAsync(Guid bucketId, CancellationToken ct);

    /// <summary>
    /// Compare-and-swap: replaces the document (null removes it) only while the stored one is still
    /// <paramref name="expected"/> (null: no policy). How edits made from the admin UI never overwrite each other.
    /// </summary>
    ValueTask<PolicyReplace> ReplaceBucketPolicyAsync(
        Guid bucketId, string? expected, string? document, DateTime updatedAt, CancellationToken ct);

    // --- Usage (metrics) -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every bucket's object count and bytes, in bucket name order. Kept up to date with every object write, so this
    /// reads one row per bucket (never the objects).
    /// </summary>
    ValueTask<IReadOnlyList<BucketUsage>> ListBucketUsageAsync(CancellationToken ct);

    /// <summary>Uploads in progress, their parts and the parts' bytes.</summary>
    ValueTask<UploadUsage> GetUploadUsageAsync(CancellationToken ct);

    // --- Blob references -----------------------------------------------------------------------------------------

    /// <summary>
    /// The ids in <paramref name="blobIds"/> that no metadata row references (orphan sweep). The one place that knows
    /// every table holding blob references (objects, object_parts, upload_parts; versions in phase 6 must be added).
    /// </summary>
    ValueTask<IReadOnlyList<Guid>> FindUnreferencedBlobsAsync(IReadOnlyList<Guid> blobIds, CancellationToken ct);

    // --- Access keys -----------------------------------------------------------------------------------------------

    /// <summary>Atomic create: false when the id is taken.</summary>
    ValueTask<bool> TryCreateAccessKeyAsync(AccessKey key, CancellationToken ct);

    /// <summary>The key (active or disabled), or null.</summary>
    ValueTask<AccessKey?> FindAccessKeyAsync(string accessKeyId, CancellationToken ct);

    /// <summary>Every key ordered by creation time.</summary>
    ValueTask<IReadOnlyList<AccessKey>> ListAccessKeysAsync(CancellationToken ct);

    /// <summary>Sets or clears <c>DisabledAt</c>; false when the key does not exist.</summary>
    ValueTask<bool> SetAccessKeyDisabledAsync(string accessKeyId, DateTime? disabledAt, CancellationToken ct);

    /// <summary>False when the key does not exist.</summary>
    ValueTask<bool> DeleteAccessKeyAsync(string accessKeyId, CancellationToken ct);

    /// <summary>Sets the key's admin API role; false when the key does not exist.</summary>
    ValueTask<bool> SetAccessKeyRoleAsync(string accessKeyId, string role, CancellationToken ct);

    // --- Admin sessions ----------------------------------------------------------------------------------------

    ValueTask CreateSessionAsync(AdminSession session, CancellationToken ct);

    /// <summary>The session with this token hash, or null (expired sessions are returned: the caller checks).</summary>
    ValueTask<AdminSession?> FindSessionAsync(byte[] tokenHash, CancellationToken ct);

    ValueTask DeleteSessionAsync(byte[] tokenHash, CancellationToken ct);

    /// <summary>Ends every session of a key (role changed, key disabled or deleted); returns how many.</summary>
    ValueTask<int> DeleteSessionsOfAsync(string accessKeyId, CancellationToken ct);

    /// <summary>Removes sessions that expired before <paramref name="now"/>; returns how many.</summary>
    ValueTask<int> DeleteExpiredSessionsAsync(DateTime now, CancellationToken ct);

    // --- Multipart uploads -----------------------------------------------------------------------------------------

    /// <summary>False when the bucket no longer exists.</summary>
    ValueTask<bool> TryCreateUploadAsync(MultipartUpload upload, CancellationToken ct);

    /// <summary>The upload, or null when it is unknown, finished, or belongs to another bucket or key.</summary>
    ValueTask<MultipartUpload?> FindUploadAsync(Guid bucketId, string key, Guid uploadId, CancellationToken ct);

    /// <summary>
    /// Stores a part (replacing the same part number) if the upload still exists, atomically; returns the replaced
    /// part's blob for the caller to delete.
    /// </summary>
    ValueTask<PartCommit> CommitPartAsync(UploadPart part, CancellationToken ct);

    /// <summary>Parts with a number greater than <paramref name="afterPartNumber"/>, in number order.</summary>
    ValueTask<PartPage> ListPartsAsync(Guid uploadId, int afterPartNumber, int maxParts, CancellationToken ct);

    /// <summary>
    /// Uploads in (key, upload id) order after the markers: with <paramref name="uploadIdMarker"/>, uploads of
    /// <paramref name="keyMarker"/> with a greater id come first; without it, the listing starts after that key.
    /// Delimiter roll-up as in <see cref="ListObjectsAsync"/> (skip-scan, common prefixes count against the limit).
    /// </summary>
    ValueTask<UploadPage> ListUploadsAsync(
        Guid bucketId, string prefix, string? delimiter, string? keyMarker, Guid? uploadIdMarker, int maxEntries,
        CancellationToken ct);

    /// <summary>Uploads started before <paramref name="createdBefore"/>, oldest first, at most <paramref name="limit"/>.</summary>
    ValueTask<IReadOnlyList<UploadRef>> ListUploadsCreatedBeforeAsync(DateTime createdBefore, int limit, CancellationToken ct);

    /// <summary>Deletes the upload and its parts; returns the part blobs to delete, or null when it is unknown.</summary>
    ValueTask<IReadOnlyList<BlobLocator>?> AbortUploadAsync(Guid bucketId, string key, Guid uploadId, CancellationToken ct);

    /// <summary>
    /// The commit point of CompleteMultipartUpload, in one transaction: checks the upload still exists and that every
    /// part in <paramref name="stored"/> (<see cref="MultipartBlobs"/>) is still the stored part (same blob), checks
    /// <paramref name="condition"/>, writes the object and its manifest, and removes the upload. Returns the replaced
    /// object's data and the unlisted parts' blobs for the caller to delete.
    /// </summary>
    ValueTask<UploadCompletion> CompleteUploadAsync(StoredObject stored, CommitCondition condition, CancellationToken ct);

    // --- Objects ---------------------------------------------------------------------------------------------------

    /// <summary>The current object, or null.</summary>
    ValueTask<StoredObject?> FindObjectAsync(Guid bucketId, string key, CancellationToken ct);

    /// <summary>
    /// The commit point of a write: inserts or replaces the object in one transaction (last commit wins) and returns
    /// the replaced data so the caller deletes its blobs. Single PUT only (<see cref="SingleBlob"/>); multipart
    /// objects are committed by <see cref="CompleteUploadAsync"/>. Nothing is written when the bucket no longer exists or the
    /// <paramref name="condition"/> does not hold; the condition is checked inside the same transaction (atomic).
    /// </summary>
    ValueTask<ObjectCommit> CommitObjectAsync(StoredObject stored, CommitCondition condition, CancellationToken ct);

    /// <summary>Deletes the object and returns its data to delete, or null when it did not exist.</summary>
    ValueTask<ObjectData?> DeleteObjectAsync(Guid bucketId, string key, CancellationToken ct);

    /// <summary>
    /// Deletes several objects in one transaction (multi-object delete) and returns the blobs to delete. Missing keys
    /// are skipped; duplicates are deleted once.
    /// </summary>
    ValueTask<IReadOnlyList<BlobLocator>> DeleteObjectsAsync(Guid bucketId, IReadOnlyList<string> keys, CancellationToken ct);

    /// <summary>
    /// One page in binary key order: keys starting with <paramref name="prefix"/> and greater than
    /// <paramref name="startAfter"/>; with a <paramref name="delimiter"/>, keys that contain it after the prefix roll
    /// up into common prefixes (counted against <paramref name="maxEntries"/>, as in Amazon S3). A common prefix equal
    /// to <paramref name="startAfter"/> is skipped entirely. Cost must not grow with the number of keys under a
    /// common prefix.
    /// </summary>
    ValueTask<ObjectPage> ListObjectsAsync(
        Guid bucketId, string prefix, string? delimiter, string? startAfter, int maxEntries, CancellationToken ct);
}

/// <summary>The outcome of <see cref="IMetadataStore.ReplaceBucketPolicyAsync"/>.</summary>
public enum PolicyReplace
{
    Replaced,

    /// <summary>Someone changed the policy since it was read: read again and retry.</summary>
    Changed,
    BucketMissing,
}
