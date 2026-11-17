namespace TimeStore.Metadata.Sqlite;

/// <summary>UTC <see cref="DateTime"/> ⇄ unix milliseconds (INTEGER columns: compact, sortable, provider neutral).</summary>
internal static class SqliteTime
{
    public static long ToUnixMs(DateTime utc) =>
        utc.Kind == DateTimeKind.Utc
            ? (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond
            : throw new ArgumentException("Timestamps must be UTC.", nameof(utc));

    public static DateTime FromUnixMs(long milliseconds) =>
        DateTime.UnixEpoch.AddTicks(milliseconds * TimeSpan.TicksPerMillisecond);
}
