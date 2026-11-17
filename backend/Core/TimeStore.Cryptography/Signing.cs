using System.Security.Cryptography;

namespace TimeStore.Cryptography;

/// <summary>
/// HMAC and hashing primitives for request signing. Static on purpose (hot path, per-request and per-chunk): span based,
/// no allocation. The one place that names the BCL crypto types, so a different implementation goes here only.
/// </summary>
public static class Signing
{
    public const int Sha256Length = 32;

    /// <summary>HMAC-SHA256 of <paramref name="data"/> with <paramref name="key"/> into <paramref name="destination"/>.</summary>
    public static int HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination) =>
        HMACSHA256.HashData(key, data, destination);

    /// <summary>SHA-256 of <paramref name="data"/> into <paramref name="destination"/>.</summary>
    public static int Sha256(ReadOnlySpan<byte> data, Span<byte> destination) => SHA256.HashData(data, destination);

    /// <summary>A reusable incremental SHA-256 (canonical requests, chunk payloads).</summary>
    public static IncrementalSha256 CreateSha256() => new(IncrementalHash.CreateHash(HashAlgorithmName.SHA256));

    /// <summary>Constant-time comparison: the only way to compare signatures, MACs or secrets.</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);
}

/// <summary>Incremental SHA-256 wrapper (keeps the BCL type out of callers).</summary>
public sealed class IncrementalSha256(IncrementalHash hash) : IDisposable
{
    public void Append(ReadOnlySpan<byte> data) => hash.AppendData(data);

    /// <summary>Writes the 32-byte digest and resets for reuse.</summary>
    public int GetHashAndReset(Span<byte> destination) => hash.GetHashAndReset(destination);

    public void Dispose() => hash.Dispose();
}
