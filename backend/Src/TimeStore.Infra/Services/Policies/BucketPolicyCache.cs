using System.Collections.Concurrent;
using TimeStore.Application.Features.Policies;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services.Policies;

/// <summary>
/// Parsed bucket policies by bucket name (also "no policy"), so authorizing a request is a dictionary lookup. Entries
/// are dropped when a policy is set or deleted and when a bucket is deleted (single node: every write goes through
/// this process).
/// </summary>
public sealed class BucketPolicyCache(IMetadataStore store)
{
    private readonly ConcurrentDictionary<string, BucketPolicy?> _policies = new(StringComparer.Ordinal);

    public async ValueTask<BucketPolicy?> FindAsync(string bucket, CancellationToken ct)
    {
        if (_policies.TryGetValue(bucket, out var cached))
            return cached;

        BucketPolicy? policy = null;
        if (await store.FindBucketAsync(bucket, ct) is { } found && await store.GetBucketPolicyAsync(found.Id, ct) is { } document)
        {
            var parsed = BucketPolicy.Parse(document, bucket);
            policy = parsed.IsSuccess ? parsed.Value : null; // validated on PUT; a stored document always parses
        }

        _policies[bucket] = policy;
        return policy;
    }

    public void Forget(string bucket) => _policies.TryRemove(bucket, out _);
}
