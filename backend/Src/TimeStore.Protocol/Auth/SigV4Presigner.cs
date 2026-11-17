using System.Globalization;
using System.Text;

namespace TimeStore.Protocol.Auth;

/// <summary>
/// Builds presigned GET URLs (SigV4 query authentication), the same form <see cref="SigV4Verifier"/> accepts and the
/// AWS SDKs produce: only <c>host</c> is signed, the payload is <c>UNSIGNED-PAYLOAD</c>.
/// </summary>
public static class SigV4Presigner
{
    /// <summary>Amazon S3's limit for a presigned URL's lifetime: 7 days.</summary>
    public const int MaxExpiresSeconds = 7 * 24 * 60 * 60;

    private const string Get = "GET";

    /// <param name="endpoint">Scheme, host and port the client will call, e.g. <c>https://s3.example.com</c>.</param>
    /// <param name="path">The decoded object path, <c>/bucket/key</c>.</param>
    /// <param name="query">Extra decoded query parameters (e.g. <c>versionId</c>); signed with the rest.</param>
    /// <param name="secretKey">Never logged; only used to derive the signing key.</param>
    public static string Presign(
        Uri endpoint, string path, IReadOnlyList<KeyValuePair<string, string>> query, string accessKeyId, string secretKey,
        string region, DateTime now, int expiresSeconds)
    {
        if (expiresSeconds is < 1 or > MaxExpiresSeconds)
            throw new ArgumentOutOfRangeException(nameof(expiresSeconds));

        var timestamp = now.ToString(SigV4Names.TimestampFormat, CultureInfo.InvariantCulture);
        var date = now.ToString(SigV4Names.DateFormat, CultureInfo.InvariantCulture);
        var scope = SigV4Canonical.Scope(date, region);
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";

        var parameters = new List<(string Name, string Value)>(query.Count + 5)
        {
            (SigV4Names.QueryAlgorithm, SigV4Names.Algorithm),
            (SigV4Names.QueryCredential, $"{accessKeyId}/{scope}"),
            (SigV4Names.QueryDate, timestamp),
            (SigV4Names.QueryExpires, expiresSeconds.ToString(CultureInfo.InvariantCulture)),
            (SigV4Names.QuerySignedHeaders, SigV4Names.Host),
        };
        foreach (var (name, value) in query)
            parameters.Add((name, value));

        // Strictly encoded and sorted: the query on the wire is already its canonical form.
        var encoded = parameters
            .Select(static p => (Name: SigV4Canonical.Encode(p.Name, keepSlash: false), Value: SigV4Canonical.Encode(p.Value, keepSlash: false)))
            .OrderBy(static p => p.Name, StringComparer.Ordinal).ThenBy(static p => p.Value, StringComparer.Ordinal)
            .ToArray();
        var canonicalQuery = string.Join('&', encoded.Select(static p => $"{p.Name}={p.Value}"));
        var canonicalUri = SigV4Canonical.Encode(path, keepSlash: true);

        var canonicalRequest = new StringBuilder(256)
            .Append(Get).Append('\n')
            .Append(canonicalUri).Append('\n')
            .Append(canonicalQuery).Append('\n')
            .Append(SigV4Names.Host).Append(':').Append(host).Append('\n')
            .Append('\n')
            .Append(SigV4Names.Host).Append('\n')
            .Append(SigV4Names.UnsignedPayload)
            .ToString();
        var stringToSign = SigV4Canonical.StringToSign(timestamp, scope, SigV4Signer.Sha256Hex(canonicalRequest));
        Span<byte> signature = stackalloc byte[32];
        SigV4Signer.Sign(SigV4Signer.DeriveSigningKey(secretKey, date, region), stringToSign, signature);

        return $"{endpoint.Scheme}://{host}{canonicalUri}?{canonicalQuery}&{SigV4Names.QuerySignature}={Convert.ToHexStringLower(signature)}";
    }
}
