namespace TimeStore.Protocol.Auth;

/// <summary>The parts of an HTTP request SigV4 needs. The Server adapts its request type to this (no ASP.NET here).</summary>
public interface ISigV4Request
{
    string Method { get; }

    /// <summary>Request target exactly as sent (path and query, still percent-encoded).</summary>
    string RawTarget { get; }

    /// <summary>Header value by lower-case name (several values joined with ','), or null when absent.</summary>
    string? GetHeader(string name);

    /// <summary>Lower-case names of every request header.</summary>
    IEnumerable<string> HeaderNames { get; }
}

/// <summary>Finds the secret for an access key id (implemented over the credential store by the host).</summary>
public interface ISecretKeyResolver
{
    ValueTask<string?> FindSecretKeyAsync(string accessKeyId, CancellationToken ct);
}

/// <param name="Region">The region clients must sign with.</param>
/// <param name="MaxClockSkew">Maximum distance between the request time and the server clock.</param>
public sealed record SigV4Options(string Region, TimeSpan MaxClockSkew);

/// <summary>An authenticated request.</summary>
/// <param name="Signature">The request signature (hex); the seed of chunk signatures for streaming uploads.</param>
/// <param name="SigningKey">Derived key for this date/region; signs chunks and trailers (never logged).</param>
/// <param name="PayloadSha256">Hex SHA-256 the body must match when <paramref name="PayloadMode"/> is Sha256.</param>
public sealed record SigV4Principal(
    string AccessKeyId,
    DateTime SignedAt,
    string Timestamp,
    string Scope,
    string Signature,
    byte[] SigningKey,
    PayloadMode PayloadMode,
    string? PayloadSha256,
    bool Presigned)
{
    /// <summary>Never prints the signature or the signing key (both are credentials).</summary>
    public override string ToString() =>
        $"{nameof(SigV4Principal)} {{ {nameof(AccessKeyId)} = {AccessKeyId}, {nameof(SignedAt)} = {SignedAt:O}, "
        + $"{nameof(PayloadMode)} = {PayloadMode}, {nameof(Presigned)} = {Presigned} }}";
}
