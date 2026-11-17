namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Lifecycle</c>: how often bucket lifecycle rules are applied.</summary>
public sealed class LifecycleSettings
{
    public const string Section = ServerOptions.Section + ":Lifecycle";
    private const int MaxIntervalMinutes = 24 * 60;

    /// <summary>Apply lifecycle rules in the background (admins can also run them at once).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between runs. Rules work in whole days, so an hour keeps objects at most an hour past due.</summary>
    public int IntervalMinutes { get; set; } = 60;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public string? Validate() =>
        IntervalMinutes is < 1 or > MaxIntervalMinutes ? $"{Section}:{nameof(IntervalMinutes)} must be 1-{MaxIntervalMinutes}." : null;
}
