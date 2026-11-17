using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using TimeStore.Application.Abstractions;

namespace TimeStore.Infra.Caching;

/// <summary>
/// <see cref="ICacheService"/> on <see cref="IMemoryCache"/> (in-process). HybridCache was the first choice, but
/// version 11.0.0 fails Native AOT publish (IL2026 in its default JSON serializer factory), so this implementation
/// keeps its two useful behaviours by hand:
/// <list type="bullet">
/// <item>single flight: concurrent misses for one key run the factory once;</item>
/// <item>tags: <see cref="RemoveByTagAsync"/> expires every entry created with a tag.</item>
/// </list>
/// Values are stored as objects (no serialization). Swap back to HybridCache here when it is AOT-clean or a
/// distributed level is needed (multi-node).
/// </summary>
public sealed class MemoryCacheService(IMemoryCache cache) : ICacheService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tags = new(StringComparer.Ordinal);

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key, Func<CancellationToken, ValueTask<T>> factory, CacheEntry entry, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out var cached) && cached is T hit)
            return hit;

        // The factory runs without the caller's token: one caller cancelling must not fail the others waiting on it.
        var flight = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(() => LoadAsync(key, factory, entry)));
        return (T)(await flight.Value.WaitAsync(ct))!;
    }

    public ValueTask RemoveAsync(string key, CancellationToken ct)
    {
        cache.Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveByTagAsync(string tag, CancellationToken ct)
    {
        // Expires every entry created with the tag. The source is not disposed: a concurrent load may still read its
        // Token, and a source without timers holds nothing that needs disposing. Removed tags leave the dictionary,
        // so it only grows with tags that are never removed (bounded by the keys callers use).
        if (_tags.TryRemove(tag, out var source))
            source.Cancel();
        return ValueTask.CompletedTask;
    }

    private async Task<object?> LoadAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, CacheEntry entry)
    {
        try
        {
            var value = await factory(CancellationToken.None);
            using (var created = cache.CreateEntry(key))
            {
                created.Value = value;
                created.AbsoluteExpirationRelativeToNow = entry.LocalExpiration ?? entry.Expiration;
                foreach (var tag in entry.Tags ?? [])
                    created.ExpirationTokens.Add(new CancellationChangeToken(TagSource(tag).Token));
            }

            return value;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private CancellationTokenSource TagSource(string tag) => _tags.GetOrAdd(tag, _ => new CancellationTokenSource());
}
