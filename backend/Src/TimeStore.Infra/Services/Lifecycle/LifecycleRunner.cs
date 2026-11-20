using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Lifecycle;
using TimeStore.Contracts.Lifecycle;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Lifecycle;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services.Lifecycle;

/// <summary>
/// Applies lifecycle rules (docs/research/s3-lifecycle.md). Per bucket, one pass over ListObjectVersions sees every
/// key's versions newest first: the current version may expire, noncurrent versions may be removed, a lone delete
/// marker may be removed. Deletions go through the same store path as client deletes (a versioned bucket gets delete
/// markers), in batches; released blobs go through the usual grace. Then uploads older than an abort rule are aborted.
/// </summary>
public sealed class LifecycleRunner(IMetadataStore store, IBlobDeletions deletions, TimeProvider clock) : ILifecycleRunner
{
    private const int PageSize = 1000;
    private const int BatchSize = 1000;
    private const int UploadPage = 1000;

    public async ValueTask<LifecycleRunResponse> RunAsync(CancellationToken ct)
    {
        var started = clock.GetUtcNow().UtcDateTime;
        var counts = new Counts();
        var buckets = (await store.ListBucketsAsync(ct)).ToDictionary(static b => b.Id);
        foreach (var bucketId in await store.ListBucketsWithLifecycleAsync(ct))
        {
            if (!buckets.TryGetValue(bucketId, out var bucket)
                || await store.GetLifecycleAsync(bucketId, ct) is not { } all)
                continue;
            var rules = all.Where(static r => r.Enabled).ToArray();
            await ExpireVersionsAsync(bucket, rules, started, counts, ct);
            await AbortUploadsAsync(bucket, rules, started, counts, ct);
        }

        return new LifecycleRunResponse(
            counts.Expired, counts.Noncurrent, counts.Markers, counts.Uploads, started, clock.GetUtcNow().UtcDateTime);
    }

    private async ValueTask ExpireVersionsAsync(Bucket bucket, LifecycleRule[] rules, DateTime now, Counts counts, CancellationToken ct)
    {
        if (!rules.Any(static r => r.ExpiresCurrent || r.NoncurrentDays is not null || r.ExpiredObjectDeleteMarker))
            return;

        var batch = new Batch(store, deletions, bucket, counts);
        var group = new List<VersionSummary>();
        string? keyMarker = null;
        VersionRef? versionMarker = null;
        while (true)
        {
            var page = await store.ListObjectVersionsAsync(bucket.Id, string.Empty, null, keyMarker, versionMarker, PageSize, ct);
            foreach (var version in page.Versions)
            {
                if (group.Count > 0 && group[0].Key != version.Key)
                {
                    await DecideAsync(group, rules, now, batch, ct);
                    group.Clear();
                }

                group.Add(version);
            }

            if (!page.IsTruncated)
                break;
            keyMarker = page.NextKeyMarker;
            versionMarker = VersionIds.TryParse(page.NextVersionIdMarker, out var next) ? next : null;
        }

        if (group.Count > 0)
            await DecideAsync(group, rules, now, batch, ct);
        await batch.FlushAsync(ct);
    }

    /// <summary>One key's versions, newest first (the first is the latest).</summary>
    private static async ValueTask DecideAsync(
        List<VersionSummary> versions, LifecycleRule[] rules, DateTime now, Batch batch, CancellationToken ct)
    {
        var latest = versions[0];

        // A delete marker that is the only version left of its key.
        if (versions.Count == 1 && latest.IsDeleteMarker
            && rules.Any(r => r.ExpiredObjectDeleteMarker && r.Filter.Prefix.Length <= latest.Key.Length
                && latest.Key.StartsWith(r.Filter.Prefix, StringComparison.Ordinal)))
        {
            await batch.AddAsync(new ObjectToDelete(latest.Key, Ref(latest)), Kind.Marker, ct);
            return;
        }

        // The current version expires: deleted (unversioned) or hidden behind a delete marker (versioned).
        if (!latest.IsDeleteMarker && rules.Any(r => r.ExpiresCurrent && r.Filter.Matches(latest.Key, latest.Size) && IsDue(r, latest.CreatedAt, now)))
        {
            var target = batch.Unversioned ? Ref(latest) : (VersionRef?)null; // exact version: never an object written since
            await batch.AddAsync(new ObjectToDelete(latest.Key, target), Kind.Expired, ct);
        }

        // Noncurrent versions: each rule keeps its newest N, the rest go once they are NoncurrentDays old.
        var noncurrent = versions.Skip(1).ToArray();
        if (noncurrent.Length == 0)
            return;
        var removed = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            if (rule.NoncurrentDays is not { } days)
                continue;
            var due = LifecycleTime.DueIfStartedBy(now, days);
            var keep = rule.NewerNoncurrentVersions ?? 0;
            for (var i = keep; i < noncurrent.Length; i++)
            {
                var version = noncurrent[i];
                if (version.SupersededAt is { } since && since <= due && rule.Filter.Matches(version.Key, version.Size)
                    && removed.Add(version.VersionId))
                    await batch.AddAsync(new ObjectToDelete(version.Key, Ref(version)), Kind.Noncurrent, ct);
            }
        }
    }

    private static bool IsDue(LifecycleRule rule, DateTime createdAt, DateTime now) =>
        rule.ExpirationDays is { } days ? createdAt <= LifecycleTime.DueIfStartedBy(now, days)
        : rule.ExpirationDate is { } date && now >= date;

    private static VersionRef Ref(VersionSummary version) =>
        version.IsNullVersion ? VersionRef.Null : VersionRef.Of(version.VersionId);

    private async ValueTask AbortUploadsAsync(Bucket bucket, LifecycleRule[] rules, DateTime now, Counts counts, CancellationToken ct)
    {
        var abortRules = rules.Where(static r => r.AbortIncompleteUploadDays is not null).ToArray();
        if (abortRules.Length == 0)
            return;

        // The shortest rule bounds the query; each upload is then checked against the rules that match its key.
        var latestDue = abortRules.Max(r => LifecycleTime.DueIfStartedBy(now, r.AbortIncompleteUploadDays!.Value));
        foreach (var upload in await store.ListUploadsCreatedBeforeAsync(latestDue.AddTicks(1), UploadPage, ct))
        {
            if (upload.BucketId != bucket.Id)
                continue;
            var stored = await store.FindUploadAsync(upload.BucketId, upload.Key, upload.UploadId, ct);
            if (stored is null || !abortRules.Any(r => upload.Key.StartsWith(r.Filter.Prefix, StringComparison.Ordinal)
                    && stored.CreatedAt <= LifecycleTime.DueIfStartedBy(now, r.AbortIncompleteUploadDays!.Value)))
                continue;
            if (await store.AbortUploadAsync(upload.BucketId, upload.Key, upload.UploadId, ct) is { } blobs)
            {
                await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
                counts.Uploads++;
            }
        }
    }

    private enum Kind
    {
        Expired,
        Noncurrent,
        Marker,
    }

    private sealed class Counts
    {
        public int Expired;
        public int Noncurrent;
        public int Markers;
        public int Uploads;
    }

    /// <summary>Deletes collected in one store transaction per <see cref="BatchSize"/>.</summary>
    private sealed class Batch(IMetadataStore store, IBlobDeletions deletions, Bucket bucket, Counts counts)
    {
        private readonly List<(ObjectToDelete Target, Kind Kind)> _pending = [];

        public bool Unversioned => bucket.Versioning == BucketVersioning.Unversioned;

        public async ValueTask AddAsync(ObjectToDelete target, Kind kind, CancellationToken ct)
        {
            _pending.Add((target, kind));
            if (_pending.Count >= BatchSize)
                await FlushAsync(ct);
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_pending.Count == 0)
                return;
            var result = await store.DeleteObjectVersionsAsync(bucket.Id, _pending.Select(static p => p.Target).ToArray(), ct);
            await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
            for (var i = 0; i < _pending.Count; i++)
            {
                var deletion = result.Results[i];
                if (deletion.Version is null && deletion.Released is null && !deletion.IsDeleteMarker)
                    continue; // nothing matched any more
                switch (_pending[i].Kind)
                {
                    case Kind.Expired:
                        counts.Expired++;
                        break;
                    case Kind.Noncurrent:
                        counts.Noncurrent++;
                        break;
                    case Kind.Marker:
                        counts.Markers++;
                        break;
                }
            }

            _pending.Clear();
        }
    }
}
