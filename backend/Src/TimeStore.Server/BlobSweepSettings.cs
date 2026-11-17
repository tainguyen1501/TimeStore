using TimeStore.Application.Features.Maintenance;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:BlobSweep</c>: the orphan-blob sweep run by the maintenance worker.</summary>
public sealed class BlobSweepSettings
{
    public const string Section = ServerOptions.Section + ":BlobSweep";
    public const int DefaultIntervalMinutes = 360;
    private const int MaxIntervalMinutes = 7 * 24 * 60;

    /// <summary>Run at startup and every <see cref="IntervalMinutes"/>. The admin trigger works either way.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>Blobs written more recently are never deleted (their commit may be in flight). At least 5.</summary>
    public int GracePeriodMinutes { get; set; } = (int)BlobSweepOptions.DefaultGracePeriod.TotalMinutes;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public TimeSpan GracePeriod => TimeSpan.FromMinutes(GracePeriodMinutes);

    public string? Validate()
    {
        if (IntervalMinutes is < 1 or > MaxIntervalMinutes)
            return $"{Section}:{nameof(IntervalMinutes)} must be 1-{MaxIntervalMinutes}.";
        if (GracePeriod < BlobSweepOptions.MinGracePeriod)
            return $"{Section}:{nameof(GracePeriodMinutes)} must be at least {BlobSweepOptions.MinGracePeriod.TotalMinutes}.";
        return null;
    }
}
