using System.Collections.Concurrent;
using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Uploads;
using TimeStore.Domain.Stores;

namespace TimeStore.UnitTests.Fakes;

/// <summary>
/// Unit-test fake for services. Real providers are verified by Tests/TimeStore.Metadata.ContractTests; listings here
/// are simple (no delimiter roll-up), enough for service tests.
/// </summary>
internal sealed class InMemoryMetadataStore : IMetadataStore
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid, string), StoredObject> _objects = new();
    private readonly ConcurrentDictionary<Guid, ReleasedBlob> _released = new();

    /// <summary>When releases are recorded (tests that move time set it).</summary>
    public Func<DateTime> Now { get; set; } = static () => DateTime.UtcNow;

    private void Release(IEnumerable<BlobLocator>? blobs)
    {
        foreach (var blob in blobs ?? [])
            _released.TryAdd(blob.BlobId, new ReleasedBlob(blob.BlobId, blob.NodeId, blob.Size, Now()));
    }

    public ValueTask<IReadOnlyList<ReleasedBlob>> ListReleasedBlobsAsync(
        string nodeId, DateTime releasedBefore, int limit, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ReleasedBlob>>(_released.Values
            .Where(b => b.NodeId == nodeId && b.ReleasedAt <= releasedBefore).OrderBy(static b => b.ReleasedAt).Take(limit).ToArray());

    public ValueTask ForgetReleasedBlobsAsync(IReadOnlyList<Guid> blobIds, CancellationToken ct)
    {
        foreach (var id in blobIds)
            _released.TryRemove(id, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<RetainedBlobs> GetRetainedBlobsAsync(CancellationToken ct) =>
        ValueTask.FromResult(new RetainedBlobs(_released.Count, _released.Values.Sum(static b => b.Size)));

    public ValueTask<Bucket?> FindBucketAsync(string name, CancellationToken ct) =>
        ValueTask.FromResult(_buckets.GetValueOrDefault(name));

    public ValueTask<IReadOnlyList<Bucket>> ListBucketsAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<Bucket>>(_buckets.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ToArray());

    public ValueTask<IReadOnlyList<BucketUsage>> ListBucketUsageAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<BucketUsage>>(_buckets.Values.OrderBy(b => b.Name, StringComparer.Ordinal)
            .Select(b =>
            {
                var objects = _objects.Values.Where(o => o.BucketId == b.Id).ToArray();
                return new BucketUsage(b.Id, b.Name, objects.Length, objects.Sum(o => o.Data.Size));
            })
            .ToArray());

    public ValueTask<UploadUsage> GetUploadUsageAsync(CancellationToken ct) => ValueTask.FromResult(new UploadUsage(0, 0, 0));

    private readonly ConcurrentDictionary<Guid, string> _policies = new();

    public ValueTask<string?> GetBucketPolicyAsync(Guid bucketId, CancellationToken ct) =>
        ValueTask.FromResult(_policies.GetValueOrDefault(bucketId));

    public ValueTask<bool> SetBucketPolicyAsync(Guid bucketId, string document, DateTime updatedAt, CancellationToken ct)
    {
        if (_buckets.Values.All(b => b.Id != bucketId))
            return ValueTask.FromResult(false);
        _policies[bucketId] = document;
        return ValueTask.FromResult(true);
    }

    public ValueTask<PolicyReplace> ReplaceBucketPolicyAsync(
        Guid bucketId, string? expected, string? document, DateTime updatedAt, CancellationToken ct)
    {
        lock (_policies)
        {
            if (_buckets.Values.All(b => b.Id != bucketId))
                return ValueTask.FromResult(PolicyReplace.BucketMissing);
            if (!string.Equals(_policies.GetValueOrDefault(bucketId), expected, StringComparison.Ordinal))
                return ValueTask.FromResult(PolicyReplace.Changed);
            if (document is null)
                _policies.TryRemove(bucketId, out _);
            else
                _policies[bucketId] = document;
            return ValueTask.FromResult(PolicyReplace.Replaced);
        }
    }

    public ValueTask DeleteBucketPolicyAsync(Guid bucketId, CancellationToken ct)
    {
        _policies.TryRemove(bucketId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SetAccessKeyScopeAsync(string accessKeyId, string scope, CancellationToken ct)
    {
        if (!_keys.TryGetValue(accessKeyId, out var key))
            return ValueTask.FromResult(false);
        _keys[accessKeyId] = key with { S3Scope = scope };
        return ValueTask.FromResult(true);
    }

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<Domain.Lifecycle.LifecycleRule>> _lifecycle = new();

    public ValueTask<IReadOnlyList<Domain.Lifecycle.LifecycleRule>?> GetLifecycleAsync(Guid bucketId, CancellationToken ct) =>
        ValueTask.FromResult(_lifecycle.GetValueOrDefault(bucketId));

    public ValueTask<bool> SetLifecycleAsync(Guid bucketId, IReadOnlyList<Domain.Lifecycle.LifecycleRule> rules, CancellationToken ct)
    {
        if (_buckets.Values.All(b => b.Id != bucketId))
            return ValueTask.FromResult(false);
        _lifecycle[bucketId] = rules;
        return ValueTask.FromResult(true);
    }

    public ValueTask DeleteLifecycleAsync(Guid bucketId, CancellationToken ct)
    {
        _lifecycle.TryRemove(bucketId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Guid>> ListBucketsWithLifecycleAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<Guid>>(_lifecycle.Where(e => e.Value.Any(r => r.Enabled)).Select(e => e.Key).ToArray());

    public ValueTask<bool> TryCreateBucketAsync(Bucket bucket, CancellationToken ct) =>
        ValueTask.FromResult(_buckets.TryAdd(bucket.Name, bucket));

    public ValueTask<BucketDeletionResult> DeleteBucketIfEmptyAsync(string name, CancellationToken ct)
    {
        lock (_uploadLock)
        {
            if (!_buckets.TryGetValue(name, out var bucket))
                return ValueTask.FromResult(new BucketDeletionResult(BucketDeletion.NotFound, []));
            if (_objects.Keys.Any(k => k.Item1 == bucket.Id))
                return ValueTask.FromResult(new BucketDeletionResult(BucketDeletion.NotEmpty, []));
            var freed = new List<BlobLocator>();
            foreach (var upload in _uploads.Values.Where(u => u.Upload.BucketId == bucket.Id).ToArray())
            {
                freed.AddRange(upload.Parts.Values.Select(p => p.Blob));
                _uploads.TryRemove(upload.Upload.UploadId, out _);
            }

            _buckets.TryRemove(name, out _);
            Release(freed);
            return ValueTask.FromResult(new BucketDeletionResult(BucketDeletion.Deleted, freed));
        }
    }

    public ValueTask<IReadOnlyList<Guid>> FindUnreferencedBlobsAsync(IReadOnlyList<Guid> blobIds, CancellationToken ct)
    {
        var referenced = _objects.Values.SelectMany(o => o.Data.Blobs).Select(b => b.BlobId)
            .Concat(_uploads.Values.SelectMany(u => u.Parts.Values).Select(p => p.Blob.BlobId))
            .Concat(_released.Keys)
            .ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<Guid>>(blobIds.Where(id => !referenced.Contains(id)).ToArray());
    }

    private readonly ConcurrentDictionary<string, AccessKey> _keys = new(StringComparer.Ordinal);

    public ValueTask<bool> TryCreateAccessKeyAsync(AccessKey key, CancellationToken ct) =>
        ValueTask.FromResult(_keys.TryAdd(key.AccessKeyId, key));

    public ValueTask<AccessKey?> FindAccessKeyAsync(string accessKeyId, CancellationToken ct) =>
        ValueTask.FromResult(_keys.GetValueOrDefault(accessKeyId));

    public ValueTask<IReadOnlyList<AccessKey>> ListAccessKeysAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AccessKey>>(_keys.Values.OrderBy(k => k.CreatedAt).ToArray());

    public ValueTask<bool> SetAccessKeyDisabledAsync(string accessKeyId, DateTime? disabledAt, CancellationToken ct)
    {
        if (!_keys.TryGetValue(accessKeyId, out var key))
            return ValueTask.FromResult(false);
        _keys[accessKeyId] = key with { DisabledAt = disabledAt };
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> DeleteAccessKeyAsync(string accessKeyId, CancellationToken ct) =>
        ValueTask.FromResult(_keys.TryRemove(accessKeyId, out _));

    public ValueTask<bool> SetAccessKeyRoleAsync(string accessKeyId, string role, CancellationToken ct)
    {
        if (!_keys.TryGetValue(accessKeyId, out var key))
            return ValueTask.FromResult(false);
        _keys[accessKeyId] = key with { Role = role };
        return ValueTask.FromResult(true);
    }

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new(StringComparer.Ordinal);

    private static string HashKey(byte[] tokenHash) => Convert.ToHexString(tokenHash);

    public ValueTask CreateSessionAsync(AdminSession session, CancellationToken ct)
    {
        _sessions[HashKey(session.TokenHash)] = session;
        return ValueTask.CompletedTask;
    }

    public ValueTask<AdminSession?> FindSessionAsync(byte[] tokenHash, CancellationToken ct) =>
        ValueTask.FromResult(_sessions.GetValueOrDefault(HashKey(tokenHash)));

    public ValueTask DeleteSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        _sessions.TryRemove(HashKey(tokenHash), out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> DeleteSessionsOfAsync(string accessKeyId, CancellationToken ct)
    {
        var count = 0;
        foreach (var (hash, session) in _sessions.ToArray())
        {
            if (session.AccessKeyId == accessKeyId && _sessions.TryRemove(hash, out _))
                count++;
        }

        return ValueTask.FromResult(count);
    }

    public ValueTask<int> DeleteExpiredSessionsAsync(DateTime now, CancellationToken ct)
    {
        var count = 0;
        foreach (var (hash, session) in _sessions.ToArray())
        {
            if (session.ExpiresAt < now && _sessions.TryRemove(hash, out _))
                count++;
        }

        return ValueTask.FromResult(count);
    }

    public ValueTask<StoredObject?> FindObjectAsync(Guid bucketId, string key, CancellationToken ct) =>
        ValueTask.FromResult(_objects.GetValueOrDefault((bucketId, key)));

    public ValueTask<ObjectCommit> CommitObjectAsync(StoredObject stored, CommitCondition condition, CancellationToken ct)
    {
        if (_buckets.Values.All(b => b.Id != stored.BucketId))
            return ValueTask.FromResult(new ObjectCommit(CommitStatus.BucketMissing, null));
        if (condition == CommitCondition.IfAbsent && !_objects.TryAdd((stored.BucketId, stored.Key), stored))
            return ValueTask.FromResult(new ObjectCommit(CommitStatus.ConditionFailed, null));
        if (condition == CommitCondition.IfAbsent)
            return ValueTask.FromResult(new ObjectCommit(CommitStatus.Committed, null));
        ObjectData? replaced = null;
        _objects.AddOrUpdate((stored.BucketId, stored.Key), stored, (_, previous) =>
        {
            replaced = previous.Data;
            return stored;
        });
        Release(replaced?.Blobs);
        return ValueTask.FromResult(new ObjectCommit(CommitStatus.Committed, replaced));
    }

    // Versioning: the fake behaves as an unversioned bucket (every object is the null version). Versioned behaviour is
    // covered by the metadata contract tests on SQLite and the end-to-end tests.
    public ValueTask<bool> SetBucketVersioningAsync(string name, BucketVersioning versioning, CancellationToken ct) =>
        throw new NotSupportedException("The in-memory store does not version objects.");

    public ValueTask<VersionLookup> FindObjectVersionAsync(Guid bucketId, string key, VersionRef version, CancellationToken ct) =>
        ValueTask.FromResult<VersionLookup>(
            version.IsNull && _objects.GetValueOrDefault((bucketId, key)) is { } stored
                ? new FoundVersion(stored, IsLatest: true)
                : VersionMissing.Instance);

    public ValueTask<VersionLookup> FindLatestDeleteMarkerAsync(Guid bucketId, string key, CancellationToken ct) =>
        ValueTask.FromResult<VersionLookup>(VersionMissing.Instance);

    public ValueTask<DeletionBatch> DeleteObjectVersionsAsync(
        Guid bucketId, IReadOnlyList<ObjectToDelete> objects, CancellationToken ct)
    {
        var results = new List<ObjectDeletion>();
        foreach (var target in objects)
        {
            var removed = target.Version is null or { IsNull: true } && _objects.TryRemove((bucketId, target.Key), out var stored)
                ? stored
                : null;
            Release(removed?.Data.Blobs);
            results.Add(new ObjectDeletion(target.Key, removed?.Data,
                removed is null ? null : new ObjectVersion(removed.VersionId, true, BucketVersioning.Unversioned), false));
        }

        return ValueTask.FromResult(new DeletionBatch(results));
    }

    public ValueTask<VersionPage> ListObjectVersionsAsync(
        Guid bucketId, string prefix, string? delimiter, string? keyMarker, VersionRef? versionMarker, int maxEntries,
        CancellationToken ct) =>
        throw new NotSupportedException("The in-memory store does not version objects.");

    public ValueTask<ObjectData?> DeleteObjectAsync(Guid bucketId, string key, CancellationToken ct)
    {
        var data = _objects.TryRemove((bucketId, key), out var removed) ? removed.Data : null;
        Release(data?.Blobs);
        return ValueTask.FromResult(data);
    }

    public ValueTask<IReadOnlyList<BlobLocator>> DeleteObjectsAsync(
        Guid bucketId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var deleted = new List<BlobLocator>();
        foreach (var key in keys)
        {
            if (_objects.TryRemove((bucketId, key), out var removed))
                deleted.AddRange(removed.Data.Blobs);
        }

        Release(deleted);

        return ValueTask.FromResult<IReadOnlyList<BlobLocator>>(deleted);
    }

    public ValueTask<ObjectPage> ListObjectsAsync(
        Guid bucketId, string prefix, string? delimiter, string? startAfter, int maxEntries, CancellationToken ct)
    {
        var keys = _objects.Values
            .Where(o => o.BucketId == bucketId && o.Key.StartsWith(prefix, StringComparison.Ordinal)
                && string.CompareOrdinal(o.Key, startAfter ?? string.Empty) > 0)
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .ToArray();
        var page = keys.Take(maxEntries).Select(o => o.ToSummary()).ToArray();
        return ValueTask.FromResult(new ObjectPage(page, [], keys.Length > maxEntries, page.LastOrDefault()?.Key));
    }

    // --- Multipart uploads (one lock: simple and atomic) ---------------------------------------------------------

    private sealed record UploadState(MultipartUpload Upload, SortedDictionary<int, UploadPart> Parts);

    private readonly ConcurrentDictionary<Guid, UploadState> _uploads = new();
    private readonly Lock _uploadLock = new();

    public ValueTask<bool> TryCreateUploadAsync(MultipartUpload upload, CancellationToken ct)
    {
        lock (_uploadLock)
        {
            if (_buckets.Values.All(b => b.Id != upload.BucketId))
                return ValueTask.FromResult(false);
            _uploads[upload.UploadId] = new UploadState(upload, []);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<MultipartUpload?> FindUploadAsync(Guid bucketId, string key, Guid uploadId, CancellationToken ct)
    {
        lock (_uploadLock)
            return ValueTask.FromResult(Find(bucketId, key, uploadId)?.Upload);
    }

    public ValueTask<PartCommit> CommitPartAsync(UploadPart part, CancellationToken ct)
    {
        lock (_uploadLock)
        {
            if (!_uploads.TryGetValue(part.UploadId, out var state))
                return ValueTask.FromResult(new PartCommit(PartCommitStatus.UploadMissing, null));
            state.Parts.TryGetValue(part.PartNumber, out var previous);
            state.Parts[part.PartNumber] = part;
            Release(previous is null ? null : [previous.Blob]);
            return ValueTask.FromResult(new PartCommit(PartCommitStatus.Committed, previous?.Blob));
        }
    }

    public ValueTask<PartPage> ListPartsAsync(Guid uploadId, int afterPartNumber, int maxParts, CancellationToken ct)
    {
        lock (_uploadLock)
        {
            var parts = _uploads.TryGetValue(uploadId, out var state)
                ? state.Parts.Values.Where(p => p.PartNumber > afterPartNumber).ToArray()
                : [];
            return ValueTask.FromResult(new PartPage(parts.Take(maxParts).ToArray(), parts.Length > maxParts));
        }
    }

    public ValueTask<UploadPage> ListUploadsAsync(
        Guid bucketId, string prefix, string? delimiter, string? keyMarker, Guid? uploadIdMarker, int maxEntries,
        CancellationToken ct)
    {
        lock (_uploadLock)
        {
            var uploads = _uploads.Values.Select(u => u.Upload)
                .Where(u => u.BucketId == bucketId && u.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(u => keyMarker is null || string.CompareOrdinal(u.Key, keyMarker) > 0
                    || (u.Key == keyMarker && uploadIdMarker is { } marker && u.UploadId.CompareTo(marker) > 0))
                .OrderBy(u => u.Key, StringComparer.Ordinal).ThenBy(u => u.UploadId)
                .Select(u => new UploadSummary(u.Key, u.UploadId, u.CreatedAt))
                .ToArray();
            var page = uploads.Take(maxEntries).ToArray();
            return ValueTask.FromResult(new UploadPage(
                page, [], uploads.Length > maxEntries, page.LastOrDefault()?.Key, page.LastOrDefault()?.UploadId));
        }
    }

    public ValueTask<IReadOnlyList<UploadRef>> ListUploadsCreatedBeforeAsync(DateTime createdBefore, int limit, CancellationToken ct)
    {
        lock (_uploadLock)
        {
            return ValueTask.FromResult<IReadOnlyList<UploadRef>>(_uploads.Values.Select(u => u.Upload)
                .Where(u => u.CreatedAt < createdBefore).OrderBy(u => u.CreatedAt).Take(limit)
                .Select(u => new UploadRef(u.BucketId, u.Key, u.UploadId)).ToArray());
        }
    }

    public ValueTask<IReadOnlyList<BlobLocator>?> AbortUploadAsync(Guid bucketId, string key, Guid uploadId, CancellationToken ct)
    {
        lock (_uploadLock)
        {
            if (Find(bucketId, key, uploadId) is not { } state)
                return ValueTask.FromResult<IReadOnlyList<BlobLocator>?>(null);
            _uploads.TryRemove(uploadId, out _);
            var parts = state.Parts.Values.Select(p => p.Blob).ToArray();
            Release(parts);
            return ValueTask.FromResult<IReadOnlyList<BlobLocator>?>(parts);
        }
    }

    public ValueTask<UploadCompletion> CompleteUploadAsync(StoredObject stored, CommitCondition condition, CancellationToken ct)
    {
        var manifest = (MultipartBlobs)stored.Data;
        lock (_uploadLock)
        {
            if (_buckets.Values.All(b => b.Id != stored.BucketId))
                return ValueTask.FromResult(new UploadCompletion(CompletionStatus.BucketMissing, null, []));
            if (Find(stored.BucketId, stored.Key, manifest.UploadId) is not { } state)
                return ValueTask.FromResult(new UploadCompletion(CompletionStatus.UploadMissing, null, []));
            if (manifest.Parts.Any(p => !state.Parts.TryGetValue(p.PartNumber, out var current) || current.Blob.BlobId != p.Blob.BlobId))
                return ValueTask.FromResult(new UploadCompletion(CompletionStatus.PartsChanged, null, []));
            _objects.TryGetValue((stored.BucketId, stored.Key), out var existing);
            if (existing is not null && condition == CommitCondition.IfAbsent)
                return ValueTask.FromResult(new UploadCompletion(CompletionStatus.ConditionFailed, null, []));

            _objects[(stored.BucketId, stored.Key)] = stored;
            _uploads.TryRemove(manifest.UploadId, out _);
            var listed = manifest.Parts.Select(p => p.PartNumber).ToHashSet();
            var discarded = state.Parts.Values.Where(p => !listed.Contains(p.PartNumber)).Select(p => p.Blob).ToArray();
            Release(existing?.Data.Blobs);
            Release(discarded);
            return ValueTask.FromResult(new UploadCompletion(CompletionStatus.Completed, existing?.Data, discarded));
        }
    }

    private UploadState? Find(Guid bucketId, string key, Guid uploadId) =>
        _uploads.TryGetValue(uploadId, out var state) && state.Upload.BucketId == bucketId && state.Upload.Key == key
            ? state
            : null;
}
