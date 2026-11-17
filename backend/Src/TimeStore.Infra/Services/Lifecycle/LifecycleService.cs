using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Lifecycle;
using TimeStore.Contracts.Lifecycle;
using TimeStore.Domain.Lifecycle;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Lifecycle;

public sealed class LifecycleService(IMetadataStore store) : ILifecycleService
{
    private const string GeneratedIdFormat = "N";

    public async ValueTask<Result<IReadOnlyList<LifecycleRule>>> GetAsync(string bucket, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (await store.GetLifecycleAsync(found.Id, ct) is not { } rules)
            return LifecycleErrors.NotFound;
        return Result<IReadOnlyList<LifecycleRule>>.Ok(rules);
    }

    public async ValueTask<Result> SetAsync(string bucket, IReadOnlyList<LifecycleRule> rules, CancellationToken ct)
    {
        // Amazon S3 names a rule sent without an id.
        var named = rules.Select(static r => string.IsNullOrEmpty(r.Id) ? r with { Id = Guid.NewGuid().ToString(GeneratedIdFormat) } : r).ToArray();
        if (LifecycleValidation.Validate(named) is { } invalid)
            return invalid;
        if (await store.FindBucketAsync(bucket, ct) is not { } found || !await store.SetLifecycleAsync(found.Id, named, ct))
            return BucketErrors.NotFound(bucket);
        return Result.Success;
    }

    public async ValueTask<Result> DeleteAsync(string bucket, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        await store.DeleteLifecycleAsync(found.Id, ct);
        return Result.Success;
    }

    public async ValueTask<Result<LifecycleConfigurationDto>> GetConfigurationAsync(string bucket, CancellationToken ct)
    {
        var rules = await GetAsync(bucket, ct);
        return rules.IsFailure ? rules.Error : ToDto(rules.Value);
    }

    public async ValueTask<Result<LifecycleConfigurationDto>> SetConfigurationAsync(
        string bucket, LifecycleConfigurationDto configuration, CancellationToken ct)
    {
        var rules = configuration.Rules.Select(static r => new LifecycleRule(
            r.Id ?? string.Empty, r.Enabled, new LifecycleFilter(r.Prefix ?? string.Empty, r.SizeGreaterThan, r.SizeLessThan),
            r.ExpirationDays, r.ExpirationDate is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null,
            r.ExpiredObjectDeleteMarker, r.NoncurrentDays, r.NewerNoncurrentVersions, r.AbortIncompleteUploadDays)).ToArray();
        var set = await SetAsync(bucket, rules, ct);
        return set.IsFailure ? set.Error : await GetConfigurationAsync(bucket, ct);
    }

    private static LifecycleConfigurationDto ToDto(IReadOnlyList<LifecycleRule> rules) => new(rules.Select(static r => new LifecycleRuleDto(
        r.Id, r.Enabled, r.Filter.Prefix, r.Filter.SizeGreaterThan, r.Filter.SizeLessThan, r.ExpirationDays, r.ExpirationDate,
        r.ExpiredObjectDeleteMarker, r.NoncurrentDays, r.NewerNoncurrentVersions, r.AbortIncompleteUploadDays)).ToArray());
}
