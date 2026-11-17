namespace TimeStore.UnitTests.Fakes;

/// <summary>A clock tests move by hand (session expiry, throttling windows).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public ManualTimeProvider() : this(FixedTimeProvider.Default)
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
