namespace TimeStore.Application.Abstractions;

/// <summary>
/// Application cache. Callers depend on this, never on the cache library (IMemoryCache today), so the library can be
/// swapped or a distributed second level added (multi-node) without touching them. Cached values must be immutable
/// (records): they are shared between callers. A distributed level will need them in a source-generated JSON context.
/// </summary>
public interface ICacheService
{
    /// <summary>The cached value for <paramref name="key"/>, or the factory's result (computed once under load).</summary>
    ValueTask<T> GetOrCreateAsync<T>(
        string key, Func<CancellationToken, ValueTask<T>> factory, CacheEntry entry, CancellationToken ct);

    ValueTask RemoveAsync(string key, CancellationToken ct);

    /// <summary>Removes every entry created with <paramref name="tag"/> (e.g. all entries of one bucket).</summary>
    ValueTask RemoveByTagAsync(string tag, CancellationToken ct);
}

/// <param name="Expiration">Overall lifetime (every cache level).</param>
/// <param name="LocalExpiration">In-process lifetime; defaults to <paramref name="Expiration"/>.</param>
/// <param name="Tags">Tags for <see cref="ICacheService.RemoveByTagAsync"/>.</param>
public sealed record CacheEntry(TimeSpan Expiration, TimeSpan? LocalExpiration = null, IReadOnlyCollection<string>? Tags = null);
