using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using TimeStore.Cryptography;

namespace TimeStore.Protocol.Auth;

/// <summary>SigV4 key derivation and signatures (span based, constant-time comparison).</summary>
public static class SigV4Signer
{
    /// <summary>HMAC chain: <c>AWS4+secret</c> → date → region → <c>s3</c> → <c>aws4_request</c>.</summary>
    /// <param name="service">Always <c>s3</c> on the server; other values only for the official SigV4 test suite.</param>
    public static byte[] DeriveSigningKey(string secretKey, string date, string region, string service = SigV4Names.Service)
    {
        Span<byte> key = stackalloc byte[Signing.Sha256Length];
        Span<byte> next = stackalloc byte[Signing.Sha256Length];

        var seed = Encoding.UTF8.GetBytes(SigV4Names.SecretPrefix + secretKey);
        try
        {
            Signing.HmacSha256(seed, Encoding.ASCII.GetBytes(date), key);
        }
        finally
        {
            Array.Clear(seed);
        }

        Signing.HmacSha256(key, Encoding.ASCII.GetBytes(region), next);
        Signing.HmacSha256(next, Encoding.ASCII.GetBytes(service), key);
        Signing.HmacSha256(key, Encoding.ASCII.GetBytes(SigV4Names.Terminator), next);
        return next.ToArray();
    }

    /// <summary>Hex SHA-256 of UTF-8 text (canonical requests).</summary>
    public static string Sha256Hex(string text)
    {
        var length = Encoding.UTF8.GetMaxByteCount(text.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var written = Encoding.UTF8.GetBytes(text, rented);
            Span<byte> hash = stackalloc byte[Signing.Sha256Length];
            Signing.Sha256(rented.AsSpan(0, written), hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>The signature of <paramref name="stringToSign"/> as raw bytes.</summary>
    public static void Sign(byte[] signingKey, string stringToSign, Span<byte> signature)
    {
        var length = Encoding.UTF8.GetMaxByteCount(stringToSign.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var written = Encoding.UTF8.GetBytes(stringToSign, rented);
            Signing.HmacSha256(signingKey, rented.AsSpan(0, written), signature);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Compares a client's hex signature with the expected bytes in constant time. Malformed hex never matches.
    /// </summary>
    public static bool Matches(string clientHex, ReadOnlySpan<byte> expected)
    {
        Span<byte> client = stackalloc byte[Signing.Sha256Length];
        return clientHex.Length == SigV4Names.SignatureHexLength
            && Convert.FromHexString(clientHex, client, out _, out var written) == OperationStatus.Done
            && written == Signing.Sha256Length
            && Signing.FixedTimeEquals(client, expected);
    }
}

/// <summary>
/// Signing keys per access key, date and region. Deterministic, so derivation (four HMACs) runs once a day per key
/// instead of once per request. A changed secret replaces the entry. Bounded LRU: when it holds more than its capacity,
/// the least recently used eighth goes at once, so eviction is rare and a burst of new keys never empties it.
/// </summary>
public sealed class SigningKeyCache
{
    public const int DefaultCapacity = 4096;
    private const int EvictionDivisor = 8;
    private readonly ConcurrentDictionary<(string AccessKeyId, string Date, string Region), Entry> _keys = new();
    private readonly Lock _evicting = new();
    private readonly int _capacity;
    private long _clock;

    public SigningKeyCache()
        : this(DefaultCapacity)
    {
    }

    public SigningKeyCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, EvictionDivisor);
        _capacity = capacity;
    }

    public int Count => _keys.Count;

    public byte[] GetOrDerive(string accessKeyId, string secretKey, string date, string region)
    {
        var id = (accessKeyId, date, region);
        var now = Interlocked.Increment(ref _clock);
        if (_keys.TryGetValue(id, out var entry) && string.Equals(entry.Secret, secretKey, StringComparison.Ordinal))
        {
            Volatile.Write(ref entry.LastUsed, now);
            return entry.Key;
        }

        var key = SigV4Signer.DeriveSigningKey(secretKey, date, region);
        _keys[id] = new Entry(secretKey, key, now);
        if (_keys.Count > _capacity)
            EvictLeastRecentlyUsed();
        return key;
    }

    /// <summary>Removes the least recently used eighth (one thread at a time; readers never wait).</summary>
    private void EvictLeastRecentlyUsed()
    {
        lock (_evicting)
        {
            if (_keys.Count <= _capacity)
                return;
            var snapshot = _keys.ToArray();
            Array.Sort(snapshot, static (a, b) => Volatile.Read(ref a.Value.LastUsed).CompareTo(Volatile.Read(ref b.Value.LastUsed)));
            var remove = snapshot.Length - _capacity + _capacity / EvictionDivisor;
            for (var i = 0; i < remove; i++)
                _keys.TryRemove(snapshot[i]);
        }
    }

    private sealed class Entry(string secret, byte[] key, long lastUsed)
    {
        public readonly string Secret = secret;
        public readonly byte[] Key = key;
        public long LastUsed = lastUsed;
    }
}
