namespace TimeStore.Domain.Lifecycle;

/// <summary>
/// Which objects a rule applies to: a key prefix and optional size bounds (exclusive, as Amazon S3).
/// <paramref name="PrefixInRule"/>: the client sent the older form (<c>Prefix</c> directly in the rule, no
/// <c>Filter</c>); Amazon S3 returns a rule in the form it was given.
/// </summary>
public sealed record LifecycleFilter(string Prefix, long? SizeGreaterThan, long? SizeLessThan, bool PrefixInRule = false)
{
    public static readonly LifecycleFilter All = new(string.Empty, null, null);

    public bool Matches(string key, long size) =>
        key.StartsWith(Prefix, StringComparison.Ordinal)
        && (SizeGreaterThan is not { } min || size > min)
        && (SizeLessThan is not { } max || size < max);
}

/// <summary>
/// One lifecycle rule (supported subset, docs/research/s3-lifecycle.md). Days count whole UTC days and round to
/// midnight as Amazon S3 does; <see cref="ExpirationDate"/> is a midnight UTC instant.
/// </summary>
public sealed record LifecycleRule(
    string Id,
    bool Enabled,
    LifecycleFilter Filter,
    int? ExpirationDays,
    DateTime? ExpirationDate,
    bool ExpiredObjectDeleteMarker,
    int? NoncurrentDays,
    int? NewerNoncurrentVersions,
    int? AbortIncompleteUploadDays)
{
    public const int MaxRules = 1000;
    public const int IdMaxLength = 255;

    public bool ExpiresCurrent => ExpirationDays is not null || ExpirationDate is not null;
}

/// <summary>When a time-based action is due (Amazon S3 rounding: the next midnight UTC after the start plus days).</summary>
public static class LifecycleTime
{
    /// <summary>The latest start time that is due now: <c>floor_midnight(now) − days</c>.</summary>
    public static DateTime DueIfStartedBy(DateTime now, int days) => now.Date.AddDays(-days);
}
