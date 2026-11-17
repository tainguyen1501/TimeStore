using Microsoft.Extensions.DependencyInjection;
using TimeStore.Application.Abstractions;
using TimeStore.Domain.Buckets;
using TimeStore.Infra;

namespace TimeStore.UnitTests.Caching;

/// <summary>The cache as the application sees it: through <see cref="ICacheService"/>, wired by AddTimeStoreServices.</summary>
public sealed class MemoryCacheServiceTests : IDisposable
{
    private static readonly CacheEntry Entry = new(TimeSpan.FromMinutes(5), Tags: ["bucket:photos"]);
    private readonly ServiceProvider _services = new ServiceCollection().AddTimeStoreServices().BuildServiceProvider();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private int _factoryCalls;

    private ICacheService Cache => _services.GetRequiredService<ICacheService>();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task GetOrCreate_SecondCall_UsesCachedValue()
    {
        var first = await Cache.GetOrCreateAsync("bucket:photos", LoadAsync, Entry, _ct);
        var second = await Cache.GetOrCreateAsync("bucket:photos", LoadAsync, Entry, _ct);

        Assert.Equal(first, second);
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public async Task Remove_ForcesReload()
    {
        await Cache.GetOrCreateAsync("bucket:photos", LoadAsync, Entry, _ct);

        await Cache.RemoveAsync("bucket:photos", _ct);
        await Cache.GetOrCreateAsync("bucket:photos", LoadAsync, Entry, _ct);

        Assert.Equal(2, _factoryCalls);
    }

    [Fact]
    public async Task RemoveByTag_ForcesReload()
    {
        await Cache.GetOrCreateAsync("bucket:photos", LoadAsync, Entry, _ct);

        await Cache.RemoveByTagAsync("bucket:photos", _ct);
        await Cache.GetOrCreateAsync("bucket:photos", LoadAsync, Entry, _ct);

        Assert.Equal(2, _factoryCalls);
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentMisses_RunFactoryOnce()
    {
        var release = new TaskCompletionSource();
        async ValueTask<Bucket> SlowLoadAsync(CancellationToken ct)
        {
            await release.Task;
            return await LoadAsync(ct);
        }

        var callers = Enumerable.Range(0, 16)
            .Select(_ => Cache.GetOrCreateAsync("bucket:photos", SlowLoadAsync, Entry, _ct).AsTask())
            .ToArray();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, _factoryCalls);
        Assert.All(results, bucket => Assert.Same(results[0], bucket));
    }

    private ValueTask<Bucket> LoadAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _factoryCalls);
        return ValueTask.FromResult(new Bucket(Guid.CreateVersion7(), "photos", new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc)));
    }
}
