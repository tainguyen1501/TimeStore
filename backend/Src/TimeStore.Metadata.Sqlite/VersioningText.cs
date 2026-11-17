using TimeStore.Domain.Objects;

namespace TimeStore.Metadata.Sqlite;

/// <summary><c>buckets.versioning</c>: NULL (never enabled), <c>Enabled</c>, <c>Suspended</c> (the S3 status words).</summary>
internal static class VersioningText
{
    public const string Enabled = "Enabled";
    public const string Suspended = "Suspended";

    public static BucketVersioning Parse(string? text) => text switch
    {
        null => BucketVersioning.Unversioned,
        Enabled => BucketVersioning.Enabled,
        Suspended => BucketVersioning.Suspended,
        _ => throw new InvalidDataException($"Unknown bucket versioning '{text}'."),
    };

    public static string? Format(BucketVersioning versioning) => versioning switch
    {
        BucketVersioning.Unversioned => null,
        BucketVersioning.Enabled => Enabled,
        BucketVersioning.Suspended => Suspended,
        _ => throw new ArgumentOutOfRangeException(nameof(versioning)),
    };
}
