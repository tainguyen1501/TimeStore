using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;
using TimeStore.Infra.Services;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Objects;

public sealed class ObjectService(IMetadataStore store, IBlobStore blobs, IBlobDeletions deletions, TimeProvider clock)
    : IObjectService
{
    public async ValueTask<Result<PutObjectResult>> PutAsync(
        PutObjectCommand command, Stream body, Func<ObjectChecksum?> verifiedChecksum, CancellationToken ct)
    {
        if (ObjectValidation.ValidatePut(command) is { } invalid)
            return invalid;

        var bucket = await store.FindBucketAsync(command.Bucket, ct);
        if (bucket is null)
            return BucketErrors.NotFound(command.Bucket);

        // Early answer without writing a blob; the commit re-checks atomically.
        if (command.OnlyIfAbsent && await store.FindObjectAsync(bucket.Id, command.Key, ct) is not null)
            return ObjectErrors.AlreadyExists(command.Key);

        // Throws on a body that breaks its integrity promise; the blob store removes its temporary file then.
        var blob = await blobs.WriteAsync(body, ct);
        var committed = false;
        try
        {
            if (command.ContentMd5 is { } md5 && !Convert.FromHexString(blob.Md5Hex).AsSpan().SequenceEqual(md5))
                return ObjectErrors.BadDigest;

            var checksum = verifiedChecksum();
            var stored = new StoredObject(
                bucket.Id, command.Key, Guid.CreateVersion7(), new SingleBlob(blob), blob.Md5Hex,
                command.ContentType ?? StoredObject.DefaultContentType, checksum?.Algorithm, checksum?.Digest,
                ChecksumType: null, command.Headers, command.UserMetadata, clock.UtcNowMs);

            var commit = await store.CommitObjectAsync(
                stored, command.OnlyIfAbsent ? CommitCondition.IfAbsent : CommitCondition.Always, ct);
            switch (commit.Status)
            {
                case CommitStatus.BucketMissing:
                    return BucketErrors.NotFound(command.Bucket);
                case CommitStatus.ConditionFailed:
                    return ObjectErrors.AlreadyExists(command.Key);
            }

            committed = true;
            if (commit.Replaced is not null)
                await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
            return new PutObjectResult(stored.ETag, checksum, commit.Version?.WriteHeader);
        }
        finally
        {
            if (!committed)
                await blobs.DeleteQuietlyAsync(blob);
        }
    }

    public async ValueTask<Result<StoredObject>> HeadAsync(string bucket, string key, CancellationToken ct)
    {
        var found = await store.FindBucketAsync(bucket, ct);
        if (found is null)
            return BucketErrors.NotFound(bucket);
        return await store.FindObjectAsync(found.Id, key, ct) is { } stored ? stored : ObjectErrors.NotFound(key);
    }

    public async ValueTask<Result<ObjectReader>> OpenAsync(string bucket, string key, CancellationToken ct)
    {
        // A concurrent overwrite may delete the blob between the lookup and the open: look up again once.
        for (var attempt = 0; ; attempt++)
        {
            var head = await HeadAsync(bucket, key, ct);
            if (head.IsFailure)
                return head.Error;
            try
            {
                var stream = head.Value.Data switch
                {
                    SingleBlob single => await blobs.OpenReadAsync(single.Blob, ct),
                    MultipartBlobs multipart => await MultipartReadStream.OpenAsync(blobs, multipart, ct),
                };
                return new ObjectReader(head.Value, stream);
            }
            catch (FileNotFoundException) when (attempt == 0)
            {
            }
        }
    }

    public async ValueTask<Result<ObjectPage>> ListAsync(
        string bucket, string prefix, string? delimiter, string? startAfter, int maxEntries, CancellationToken ct)
    {
        var found = await store.FindBucketAsync(bucket, ct);
        if (found is null)
            return BucketErrors.NotFound(bucket);
        return await store.ListObjectsAsync(found.Id, prefix, delimiter, startAfter, maxEntries, ct);
    }

    public async ValueTask<Result<ObjectDeleted>> DeleteAsync(string bucket, string key, string? versionId, CancellationToken ct)
    {
        var deleted = await DeleteManyAsync(bucket, [new DeleteObjectEntry(key, versionId)], ct);
        if (deleted.IsFailure)
            return deleted.Error;
        var result = deleted.Value[0];
        return result.Error is { } error ? error : result;
    }

    public async ValueTask<Result<IReadOnlyList<ObjectDeleted>>> DeleteManyAsync(
        string bucket, IReadOnlyList<DeleteObjectEntry> entries, CancellationToken ct)
    {
        if (entries.Count > ObjectErrors.MaxDeleteKeys)
            return ObjectErrors.TooManyKeys;
        var found = await store.FindBucketAsync(bucket, ct);
        if (found is null)
            return BucketErrors.NotFound(bucket);

        var results = new ObjectDeleted?[entries.Count];
        var targets = new List<ObjectToDelete>(entries.Count);
        var positions = new List<int>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            VersionRef? version = null;
            if (entry.VersionId is not null)
            {
                if (!VersionIds.TryParse(entry.VersionId, out var parsed))
                {
                    results[i] = new ObjectDeleted(entry.Key, entry.VersionId, false, ObjectErrors.InvalidVersionId);
                    continue;
                }

                version = parsed;
            }

            targets.Add(new ObjectToDelete(entry.Key, version));
            positions.Add(i);
        }

        if (targets.Count > 0)
        {
            var batch = await store.DeleteObjectVersionsAsync(found.Id, targets, ct);
            await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
            for (var j = 0; j < batch.Results.Count; j++)
            {
                var deletion = batch.Results[j];
                var requested = entries[positions[j]].VersionId;
                // A named version is echoed; a delete of the latest reports the marker it created (versioned buckets).
                var versionId = requested ?? (deletion.IsDeleteMarker ? deletion.Version?.Header : null);
                results[positions[j]] = new ObjectDeleted(deletion.Key, versionId, deletion.IsDeleteMarker);
            }
        }

        return results.Select(static r => r!).ToArray();
    }

    public async ValueTask<Result<ObjectVersionHead>> HeadVersionAsync(
        string bucket, string key, string? versionId, CancellationToken ct)
    {
        var found = await store.FindBucketAsync(bucket, ct);
        if (found is null)
            return BucketErrors.NotFound(bucket);

        if (versionId is null)
        {
            if (await store.FindObjectAsync(found.Id, key, ct) is { } current)
                return new ObjectVersionHead(current, Header(found, current), null);
            return await store.FindLatestDeleteMarkerAsync(found.Id, key, ct) is FoundDeleteMarker marker
                ? new ObjectVersionHead(null, marker.VersionIdText, new DeleteMarkerHit(marker.VersionIdText, marker.CreatedAt, Named: false))
                : ObjectErrors.NotFound(key);
        }

        if (!VersionIds.TryParse(versionId, out var version))
            return ObjectErrors.InvalidVersionId;
        return await store.FindObjectVersionAsync(found.Id, key, version, ct) switch
        {
            FoundVersion v => new ObjectVersionHead(v.Object, versionId, null),
            FoundDeleteMarker m => new ObjectVersionHead(null, versionId, new DeleteMarkerHit(versionId, m.CreatedAt, Named: true)),
            VersionMissing => ObjectErrors.NoSuchVersion,
        };
    }

    public async ValueTask<Result<ObjectVersionOpen>> OpenVersionAsync(
        string bucket, string key, string? versionId, CancellationToken ct)
    {
        // A concurrent overwrite may delete the blob between the lookup and the open: look up again once (latest only:
        // a named version's blobs are never released while it exists).
        for (var attempt = 0; ; attempt++)
        {
            var head = await HeadVersionAsync(bucket, key, versionId, ct);
            if (head.IsFailure)
                return head.Error;
            if (head.Value.Object is not { } stored)
                return new ObjectVersionOpen(null, head.Value.VersionId, head.Value.Marker);
            try
            {
                var stream = stored.Data switch
                {
                    SingleBlob single => await blobs.OpenReadAsync(single.Blob, ct),
                    MultipartBlobs multipart => await MultipartReadStream.OpenAsync(blobs, multipart, ct),
                };
                return new ObjectVersionOpen(new ObjectReader(stored, stream), head.Value.VersionId, null);
            }
            catch (FileNotFoundException) when (attempt == 0 && versionId is null)
            {
            }
        }
    }

    public async ValueTask<Result<VersionPage>> ListVersionsAsync(
        string bucket, string prefix, string? delimiter, string? keyMarker, string? versionIdMarker, int maxEntries,
        CancellationToken ct)
    {
        var found = await store.FindBucketAsync(bucket, ct);
        if (found is null)
            return BucketErrors.NotFound(bucket);

        VersionRef? marker = null;
        if (!string.IsNullOrEmpty(versionIdMarker))
        {
            if (!VersionIds.TryParse(versionIdMarker, out var parsed))
                return ObjectErrors.InvalidVersionId;
            marker = parsed;
        }

        return await store.ListObjectVersionsAsync(found.Id, prefix, delimiter, keyMarker, marker, maxEntries, ct);
    }

    /// <summary>The <c>x-amz-version-id</c> of the current object: none in a never-versioned bucket.</summary>
    private static string? Header(Domain.Buckets.Bucket bucket, StoredObject stored) =>
        bucket.Versioning == BucketVersioning.Unversioned ? null : VersionIds.Format(stored.VersionId, stored.IsNullVersion);
}
