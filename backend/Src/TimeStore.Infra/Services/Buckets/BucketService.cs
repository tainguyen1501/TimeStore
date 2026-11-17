using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Buckets;
using TimeStore.Contracts.Buckets;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Buckets;

public sealed class BucketService(
    IMetadataStore store, IBlobDeletions deletions, Policies.BucketPolicyCache policies, TimeProvider clock) : IBucketService
{
    public async ValueTask<Result<BucketResponse>> CreateAsync(CreateBucketRequest request, CancellationToken ct)
    {
        if (BucketValidation.Validate(request) is { } invalid)
            return invalid;

        var bucket = new Bucket(Guid.CreateVersion7(), request.Name, clock.UtcNowMs);
        if (!await store.TryCreateBucketAsync(bucket, ct))
            return BucketErrors.AlreadyExists(request.Name);

        return bucket.ToResponse();
    }

    public async ValueTask<IReadOnlyList<BucketResponse>> ListAsync(CancellationToken ct)
    {
        var buckets = await store.ListBucketsAsync(ct);
        var responses = new BucketResponse[buckets.Count];
        for (var i = 0; i < responses.Length; i++)
            responses[i] = buckets[i].ToResponse();

        return responses;
    }

    public async ValueTask<Result<BucketResponse>> GetAsync(string name, CancellationToken ct) =>
        await store.FindBucketAsync(name, ct) is { } bucket ? bucket.ToResponse() : BucketErrors.NotFound(name);

    public async ValueTask<Result<BucketVersioning>> GetVersioningAsync(string name, CancellationToken ct) =>
        await store.FindBucketAsync(name, ct) is { } bucket ? bucket.Versioning : BucketErrors.NotFound(name);

    public async ValueTask<Result<BucketResponse>> SetVersioningAsync(
        string name, SetBucketVersioningRequest request, CancellationToken ct)
    {
        BucketVersioning versioning;
        switch (request.Status)
        {
            case nameof(BucketVersioning.Enabled):
                versioning = BucketVersioning.Enabled;
                break;
            case nameof(BucketVersioning.Suspended):
                versioning = BucketVersioning.Suspended;
                break;
            default:
                return BucketErrors.InvalidVersioning;
        }
        var set = await SetVersioningAsync(name, versioning, ct);
        if (set.IsFailure)
            return set.Error;
        return await GetAsync(name, ct);
    }

    public async ValueTask<Result> SetVersioningAsync(string name, BucketVersioning versioning, CancellationToken ct) =>
        await store.SetBucketVersioningAsync(name, versioning, ct) ? Result.Success : BucketErrors.NotFound(name);

    /// <summary>Uploads still in progress are aborted with the bucket (Amazon S3); their parts are freed after the commit.</summary>
    public async ValueTask<Result> DeleteAsync(string name, CancellationToken ct)
    {
        var deletion = await store.DeleteBucketIfEmptyAsync(name, ct);
        switch (deletion.Status)
        {
            case BucketDeletion.Deleted:
                policies.Forget(name); // a new bucket with this name starts without the old policy
                await deletions.DeferAsync(deletion.FreedBlobs);
                return Result.Success;
            case BucketDeletion.NotEmpty:
                return BucketErrors.NotEmpty(name);
            default:
                return BucketErrors.NotFound(name);
        }
    }
}
