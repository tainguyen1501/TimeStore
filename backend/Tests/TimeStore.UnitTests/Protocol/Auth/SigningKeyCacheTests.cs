using TimeStore.Protocol.Auth;

namespace TimeStore.UnitTests.Protocol.Auth;

public sealed class SigningKeyCacheTests
{
    private const string Date = "20261116";
    private const string Region = "us-east-1";
    private const string Secret = "secret-key-123";

    [Fact]
    public void ASecondLookup_ReturnsTheSameDerivedKey()
    {
        var cache = new SigningKeyCache();
        var first = cache.GetOrDerive("AKID", Secret, Date, Region);
        Assert.Same(first, cache.GetOrDerive("AKID", Secret, Date, Region));
        Assert.Equal(SigV4Signer.DeriveSigningKey(Secret, Date, Region), first);
    }

    [Fact]
    public void AChangedSecret_DerivesANewKey()
    {
        var cache = new SigningKeyCache();
        var old = cache.GetOrDerive("AKID", Secret, Date, Region);
        var rotated = cache.GetOrDerive("AKID", "another-secret", Date, Region);
        Assert.NotEqual(old, rotated);
        Assert.Equal(SigV4Signer.DeriveSigningKey("another-secret", Date, Region), rotated);
    }

    [Fact]
    public void OverCapacity_TheLeastRecentlyUsedGo_HotKeysStay()
    {
        var cache = new SigningKeyCache(capacity: 16);
        var hot = cache.GetOrDerive("hot", Secret, Date, Region);

        for (var i = 0; i < 100; i++)
        {
            cache.GetOrDerive($"cold-{i}", Secret, Date, Region);
            cache.GetOrDerive("hot", Secret, Date, Region); // used between every new key
        }

        Assert.InRange(cache.Count, 1, 16);
        Assert.Same(hot, cache.GetOrDerive("hot", Secret, Date, Region));
    }

    [Fact]
    public void ConcurrentUse_StaysBoundedAndCorrect()
    {
        var cache = new SigningKeyCache(capacity: 64);
        Parallel.For(0, 5_000, i =>
        {
            var id = $"key-{i % 200}";
            Assert.Equal(SigV4Signer.DeriveSigningKey(Secret + id, Date, Region), cache.GetOrDerive(id, Secret + id, Date, Region));
        });
        Assert.InRange(cache.Count, 1, 64 + Environment.ProcessorCount);
    }
}
