namespace TimeStore.Contracts.Lifecycle;

/// <summary>
/// One lifecycle rule for the admin API (the supported subset of Amazon S3, docs/research/s3-lifecycle.md).
/// <paramref name="Id"/> may be empty on a request: the server generates one.
/// </summary>
public sealed record LifecycleRuleDto(
    string? Id,
    bool Enabled,
    string? Prefix,
    long? SizeGreaterThan,
    long? SizeLessThan,
    int? ExpirationDays,
    DateTime? ExpirationDate,
    bool ExpiredObjectDeleteMarker,
    int? NoncurrentDays,
    int? NewerNoncurrentVersions,
    int? AbortIncompleteUploadDays);

/// <summary>A bucket's whole lifecycle configuration (a PUT replaces it).</summary>
public sealed record LifecycleConfigurationDto(IReadOnlyList<LifecycleRuleDto> Rules);

/// <summary>What one lifecycle run did.</summary>
public sealed record LifecycleRunResponse(
    int Expired, int NoncurrentRemoved, int DeleteMarkersRemoved, int UploadsAborted, DateTime StartedAt, DateTime FinishedAt);
