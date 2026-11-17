using TimeStore.Contracts.Lifecycle;
using TimeStore.Domain.Lifecycle;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Lifecycle;

/// <summary>A bucket's lifecycle configuration, for the S3 API (Domain rules) and the admin API (Contracts).</summary>
public interface ILifecycleService
{
    /// <summary>The rules, or <c>LIFECYCLE_NOT_FOUND</c> when the bucket has none.</summary>
    ValueTask<Result<IReadOnlyList<LifecycleRule>>> GetAsync(string bucket, CancellationToken ct);

    /// <summary>Validates (rule ids are generated where empty) and replaces the configuration.</summary>
    ValueTask<Result> SetAsync(string bucket, IReadOnlyList<LifecycleRule> rules, CancellationToken ct);

    /// <summary>Removes the configuration; succeeds when there is none (Amazon S3 answers 204 either way).</summary>
    ValueTask<Result> DeleteAsync(string bucket, CancellationToken ct);

    ValueTask<Result<LifecycleConfigurationDto>> GetConfigurationAsync(string bucket, CancellationToken ct);

    ValueTask<Result<LifecycleConfigurationDto>> SetConfigurationAsync(
        string bucket, LifecycleConfigurationDto configuration, CancellationToken ct);
}

/// <summary>Applies every bucket's enabled rules once (hosted worker; admin "run now").</summary>
public interface ILifecycleRunner
{
    ValueTask<LifecycleRunResponse> RunAsync(CancellationToken ct);
}

public static class LifecycleErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string NotFound = "LIFECYCLE_NOT_FOUND";
        public const string Invalid = "LIFECYCLE_INVALID";
        public const string Malformed = "LIFECYCLE_MALFORMED";
        public const string NotImplemented = "LIFECYCLE_NOT_IMPLEMENTED";
        public const string StorageClass = "LIFECYCLE_STORAGE_CLASS";
    }

    public static readonly AppError NotFound =
        AppError.NotFound(Codes.NotFound, "The lifecycle configuration does not exist");

    public static AppError Invalid(string reason) => AppError.Validation(Codes.Invalid, reason);

    public static AppError Malformed(string reason) => AppError.Validation(Codes.Malformed, reason);

    /// <summary>A rule filters on object tags, which TimeStore does not have.</summary>
    public static readonly AppError TagFilters = AppError.Validation(
        Codes.NotImplemented, "Lifecycle tag filters are not implemented: TimeStore has no object tags.");

    /// <summary>A rule moves objects to another storage class; TimeStore has one (Amazon S3: InvalidStorageClass).</summary>
    public static readonly AppError Transitions = AppError.Validation(
        Codes.StorageClass, "Transitions are not supported: TimeStore has one storage class (STANDARD).");
}

/// <summary>The rules Amazon S3 enforces on a configuration (the supported subset).</summary>
public static class LifecycleValidation
{
    public const int MaxNewerNoncurrentVersions = 100;

    public static AppError? Validate(IReadOnlyList<LifecycleRule> rules)
    {
        if (rules.Count == 0)
            return LifecycleErrors.Malformed("A lifecycle configuration needs at least one rule.");
        if (rules.Count > LifecycleRule.MaxRules)
            return LifecycleErrors.Invalid($"A lifecycle configuration has at most {LifecycleRule.MaxRules} rules.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule.Id.Length > LifecycleRule.IdMaxLength)
                return LifecycleErrors.Invalid($"The rule id must be at most {LifecycleRule.IdMaxLength} characters.");
            if (!ids.Add(rule.Id))
                return LifecycleErrors.Invalid($"Rule id '{rule.Id}' is used by more than one rule.");
            if (Check(rule) is { } problem)
                return LifecycleErrors.Invalid($"Rule '{rule.Id}': {problem}");
        }

        return null;
    }

    private static string? Check(LifecycleRule rule)
    {
        if (!rule.ExpiresCurrent && !rule.ExpiredObjectDeleteMarker && rule.NoncurrentDays is null && rule.AbortIncompleteUploadDays is null)
            return "at least one action is required.";
        if (rule.ExpirationDays is not null && rule.ExpirationDate is not null)
            return "Expiration takes Days or Date, not both.";
        if (rule.ExpiredObjectDeleteMarker && rule.ExpiresCurrent)
            return "ExpiredObjectDeleteMarker cannot be combined with Days or Date.";
        if (rule.ExpirationDays is <= 0 || rule.NoncurrentDays is <= 0 || rule.AbortIncompleteUploadDays is <= 0)
            return "days must be positive integers.";
        if (rule.ExpirationDate is { } date && (date.Kind != DateTimeKind.Utc || date.TimeOfDay != TimeSpan.Zero))
            return "Date must be midnight UTC.";
        if (rule.NewerNoncurrentVersions is { } newer
            && (newer is < 1 or > MaxNewerNoncurrentVersions || rule.NoncurrentDays is null))
            return $"NewerNoncurrentVersions must be 1-{MaxNewerNoncurrentVersions} and needs NoncurrentDays.";
        if (rule.Filter is { SizeGreaterThan: < 0 } or { SizeLessThan: < 0 }
            || rule.Filter is { SizeGreaterThan: { } min, SizeLessThan: { } max } && min >= max)
            return "ObjectSizeGreaterThan must be below ObjectSizeLessThan, both non-negative.";
        return null;
    }
}
