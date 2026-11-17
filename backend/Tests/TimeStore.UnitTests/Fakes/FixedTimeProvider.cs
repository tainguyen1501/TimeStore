namespace TimeStore.UnitTests.Fakes;

/// <summary>A clock that returns a fixed time (with sub-millisecond ticks, to exercise truncation).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public static readonly DateTimeOffset Default =
        new DateTimeOffset(2026, 11, 16, 9, 30, 15, 123, TimeSpan.Zero).AddTicks(4567);

    public FixedTimeProvider() : this(Default)
    {
    }

    public override DateTimeOffset GetUtcNow() => now;
}
