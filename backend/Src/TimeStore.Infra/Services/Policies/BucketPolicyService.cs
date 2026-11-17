using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Policies;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Policies;

public sealed class BucketPolicyService(IMetadataStore store, BucketPolicyCache cache, TimeProvider clock) : IBucketPolicyService
{
    public async ValueTask<Result<string>> GetAsync(string bucket, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (await store.GetBucketPolicyAsync(found.Id, ct) is not { } document)
            return PolicyErrors.NotFound;
        return document;
    }

    public async ValueTask<Result> SetAsync(string bucket, string document, CancellationToken ct)
    {
        var parsed = BucketPolicy.Parse(document, bucket);
        if (parsed.IsFailure)
            return parsed.Error;
        if (await store.FindBucketAsync(bucket, ct) is not { } found
            || !await store.SetBucketPolicyAsync(found.Id, document, clock.UtcNowMs, ct))
            return BucketErrors.NotFound(bucket);
        cache.Forget(bucket);
        return Result.Success;
    }

    public async ValueTask<Result> DeleteAsync(string bucket, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        await store.DeleteBucketPolicyAsync(found.Id, ct);
        cache.Forget(bucket);
        return Result.Success;
    }

    private const int ReplaceAttempts = 5;

    public async ValueTask<Result<IReadOnlyList<string>>> GetPublicPrefixesAsync(string bucket, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        return Result<IReadOnlyList<string>>.Ok(PublicAccess.Prefixes(await store.GetBucketPolicyAsync(found.Id, ct), bucket));
    }

    public async ValueTask<Result<IReadOnlyList<string>>> SetPublicAsync(string bucket, string prefix, bool isPublic, CancellationToken ct)
    {
        if (PublicAccess.Validate(prefix) is { } invalid)
            return invalid;
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);

        // Read, edit, compare-and-swap: a concurrent edit (another admin, an S3 PutBucketPolicy) is never overwritten.
        for (var attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            var current = await store.GetBucketPolicyAsync(found.Id, ct);
            var next = PublicAccess.Apply(current, bucket, prefix, isPublic);
            if (next is not null && BucketPolicy.Parse(next, bucket) is { IsFailure: true } parsed)
                return parsed.Error; // e.g. over 20 KB, or the existing policy is no longer valid
            if (next == current)
                return Result<IReadOnlyList<string>>.Ok(PublicAccess.Prefixes(current, bucket));

            switch (await store.ReplaceBucketPolicyAsync(found.Id, current, next, clock.UtcNowMs, ct))
            {
                case PolicyReplace.Replaced:
                    cache.Forget(bucket);
                    return Result<IReadOnlyList<string>>.Ok(PublicAccess.Prefixes(next, bucket));
                case PolicyReplace.BucketMissing:
                    return BucketErrors.NotFound(bucket);
            }
        }

        return PolicyErrors.Conflict;
    }

    public async ValueTask<Result<bool>> IsPublicAsync(string bucket, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is null)
            return BucketErrors.NotFound(bucket);
        return (await cache.FindAsync(bucket, ct))?.IsPublic ?? false;
    }
}

public sealed class S3Authorizer(BucketPolicyCache cache) : IS3Authorizer
{
    public async ValueTask<bool> IsAllowedAsync(
        string? bucket, string action, string resource, S3Caller caller, Func<IReadOnlyDictionary<string, string>> context,
        CancellationToken ct)
    {
        if (caller.IsRoot)
            return true; // break-glass: a wrong policy can always be fixed with the root key

        var fullKey = !caller.IsAnonymous && caller.S3Scope == Domain.AccessKeys.AccessKeyScopes.Full;
        if (bucket is null || await cache.FindAsync(bucket, ct) is not { } policy)
            return fullKey;

        return policy.Evaluate(new PolicyRequest(action, resource, caller.AccessKeyId, context())) switch
        {
            PolicyDecision.Deny => false,
            PolicyDecision.Allow => true,
            _ => fullKey,
        };
    }
}
