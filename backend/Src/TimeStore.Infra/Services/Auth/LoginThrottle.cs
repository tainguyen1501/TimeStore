using System.Collections.Concurrent;

namespace TimeStore.Infra.Services.Auth;

/// <summary>
/// Slows down repeated failed sign-ins, per access key id and per client address (OWASP: delay, do not lock out:
/// a lockout would let anyone lock the admin out). After <see cref="FreeFailures"/> failures within
/// <see cref="Window"/>, each further attempt must wait, doubling from one second up to <see cref="MaxDelay"/>.
/// In-process (single node); bounded: old entries are dropped when the table grows.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int FreeFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);
    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(int Failures, DateTimeOffset FirstFailure, DateTimeOffset BlockedUntil);

    /// <summary>How long the caller must wait before trying again, or null when it may try now.</summary>
    public TimeSpan? RetryAfter(IEnumerable<string> keys)
    {
        var now = clock.GetUtcNow();
        TimeSpan? longest = null;
        foreach (var key in keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.BlockedUntil > now)
            {
                var wait = entry.BlockedUntil - now;
                if (longest is null || wait > longest)
                    longest = wait;
            }
        }

        return longest;
    }

    public void Failed(IEnumerable<string> keys)
    {
        var now = clock.GetUtcNow();
        if (_entries.Count > MaxEntries)
            Prune(now);
        foreach (var key in keys)
        {
            _entries.AddOrUpdate(key,
                _ => new Entry(1, now, now),
                (_, previous) =>
                {
                    var failures = now - previous.FirstFailure > Window ? 1 : previous.Failures + 1;
                    var first = failures == 1 ? now : previous.FirstFailure;
                    var blocked = failures > FreeFailures ? now + Delay(failures - FreeFailures) : now;
                    return new Entry(failures, first, blocked);
                });
        }
    }

    public void Succeeded(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            _entries.TryRemove(key, out _);
    }

    private static TimeSpan Delay(int beyondFree)
    {
        var seconds = Math.Pow(2, Math.Min(beyondFree - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.FirstFailure > Window && entry.BlockedUntil <= now)
                _entries.TryRemove(key, out _);
        }
    }
}
