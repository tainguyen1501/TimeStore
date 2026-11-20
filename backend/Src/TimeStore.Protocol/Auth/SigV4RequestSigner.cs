using System.Globalization;

namespace TimeStore.Protocol.Auth;

/// <summary>
/// Signs requests TimeStore sends to another S3 endpoint (replication) with SigV4 in the <c>Authorization</c> header,
/// the form <see cref="SigV4Verifier"/> and Amazon S3 accept. The secret only derives the signing key; nothing here
/// logs or returns it.
/// </summary>
public static class SigV4RequestSigner
{
    /// <summary>
    /// Adds <c>x-amz-date</c> and <c>x-amz-content-sha256</c> to <paramref name="headers"/> and returns the
    /// <c>Authorization</c> value. Every header in <paramref name="headers"/> (with <c>host</c>) is signed: Amazon S3
    /// requires every <c>x-amz-*</c> header to be.
    /// </summary>
    /// <param name="canonicalUri">The path exactly as sent (already encoded: <see cref="EncodePath"/>).</param>
    /// <param name="canonicalQuery">The query exactly as sent (encoded and sorted: <see cref="Query"/>).</param>
    /// <param name="headers">Header names (any case) and values to sign; changed in place.</param>
    /// <param name="payloadHash">Hex SHA-256 of the body, or <c>UNSIGNED-PAYLOAD</c>.</param>
    public static string Sign(
        string method, string canonicalUri, string canonicalQuery, IDictionary<string, string> headers, string payloadHash,
        string accessKeyId, string secretKey, string region, DateTime now)
    {
        var timestamp = now.ToString(SigV4Names.TimestampFormat, CultureInfo.InvariantCulture);
        var date = now.ToString(SigV4Names.DateFormat, CultureInfo.InvariantCulture);
        headers[SigV4Names.AmzDate] = timestamp;
        headers[SigV4Names.ContentSha256] = payloadHash;

        var signed = headers
            .Select(static h => KeyValuePair.Create(h.Key.ToLowerInvariant(), h.Value))
            .OrderBy(static h => h.Key, StringComparer.Ordinal)
            .ToArray();
        var scope = SigV4Canonical.Scope(date, region);
        var canonicalRequest = SigV4Canonical.CanonicalRequest(method, canonicalUri, canonicalQuery, signed, payloadHash);
        var stringToSign = SigV4Canonical.StringToSign(timestamp, scope, SigV4Signer.Sha256Hex(canonicalRequest));
        Span<byte> signature = stackalloc byte[32];
        SigV4Signer.Sign(SigV4Signer.DeriveSigningKey(secretKey, date, region), stringToSign, signature);

        return $"{SigV4Names.Algorithm} {SigV4Names.CredentialPart}{accessKeyId}/{scope}, "
            + $"{SigV4Names.SignedHeadersPart}{string.Join(';', signed.Select(static h => h.Key))}, "
            + $"{SigV4Names.SignaturePart}{Convert.ToHexStringLower(signature)}";
    }

    /// <summary>Path-style object path <c>/bucket/key</c>, encoded (slashes of the key kept), as sent and signed.</summary>
    public static string EncodePath(string bucket, string? key = null) =>
        key is null
            ? "/" + SigV4Canonical.Encode(bucket, keepSlash: false)
            : "/" + SigV4Canonical.Encode(bucket, keepSlash: false) + "/" + SigV4Canonical.Encode(key, keepSlash: true);

    /// <summary>A query string encoded and sorted, as sent and signed (a value-less parameter is <c>name=</c>).</summary>
    public static string Query(params ReadOnlySpan<(string Name, string Value)> parameters)
    {
        var encoded = new List<(string Name, string Value)>(parameters.Length);
        foreach (var (name, value) in parameters)
            encoded.Add((SigV4Canonical.Encode(name, keepSlash: false), SigV4Canonical.Encode(value, keepSlash: false)));
        encoded.Sort(static (a, b) =>
            string.CompareOrdinal(a.Name, b.Name) is var byName and not 0 ? byName : string.CompareOrdinal(a.Value, b.Value));
        return string.Join('&', encoded.Select(static p => $"{p.Name}={p.Value}"));
    }
}
