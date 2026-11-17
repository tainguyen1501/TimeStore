using System.Text;
using TimeStore.Protocol.Routing;

namespace TimeStore.Protocol.Auth;

/// <summary>Builds the SigV4 canonical request and string to sign (S3 rules: the path is encoded once).</summary>
public static class SigV4Canonical
{
    private const string HexUpper = "0123456789ABCDEF";

    /// <summary>
    /// Canonical URI from the raw path: decode, then encode every byte except RFC 3986 unreserved characters and '/'.
    /// This is what SDKs sign, whatever escaping the client chose on the wire. Null when the path cannot be decoded.
    /// </summary>
    public static string? CanonicalUri(ReadOnlySpan<char> rawPath)
    {
        if (!S3PercentDecoder.TryDecode(rawPath, plusAsSpace: false, out var decoded))
            return null;
        return Encode(decoded, keepSlash: true);
    }

    /// <summary>
    /// Canonical query: every parameter (duplicates kept) except <paramref name="exclude"/>, names and values decoded
    /// then strictly encoded, sorted by name then value, joined with '&amp;'. Flags become <c>name=</c>.
    /// </summary>
    public static string? CanonicalQuery(ReadOnlySpan<char> rawQuery, string? exclude)
    {
        if (rawQuery.IsEmpty)
            return string.Empty;

        var pairs = new List<(string Name, string Value)>();
        foreach (var range in rawQuery.Split('&'))
        {
            var pair = rawQuery[range];
            if (pair.IsEmpty)
                continue;
            var equals = pair.IndexOf('=');
            var rawName = equals < 0 ? pair : pair[..equals];
            var rawValue = equals < 0 ? ReadOnlySpan<char>.Empty : pair[(equals + 1)..];
            if (!S3PercentDecoder.TryDecode(rawName, plusAsSpace: true, out var name)
                || !S3PercentDecoder.TryDecode(rawValue, plusAsSpace: true, out var value))
                return null;
            if (exclude is not null && string.Equals(name, exclude, StringComparison.Ordinal))
                continue;
            pairs.Add((Encode(name, keepSlash: false), Encode(value, keepSlash: false)));
        }

        pairs.Sort(static (a, b) =>
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Value, b.Value);
        });

        var builder = new StringBuilder();
        foreach (var (name, value) in pairs)
        {
            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(name).Append('=').Append(value);
        }

        return builder.ToString();
    }

    /// <summary>The full canonical request.</summary>
    public static string CanonicalRequest(
        string method, string canonicalUri, string canonicalQuery, ISigV4Request request,
        IReadOnlyList<string> signedHeaders, string payloadHash)
    {
        var builder = new StringBuilder(256);
        builder.Append(method).Append('\n')
            .Append(canonicalUri).Append('\n')
            .Append(canonicalQuery).Append('\n');
        foreach (var name in signedHeaders)
        {
            builder.Append(name).Append(':');
            AppendTrimmed(builder, request.GetHeader(name));
            builder.Append('\n');
        }

        builder.Append('\n').AppendJoin(';', signedHeaders).Append('\n').Append(payloadHash);
        return builder.ToString();
    }

    /// <summary>The string to sign: algorithm, timestamp, scope, hex SHA-256 of the canonical request.</summary>
    public static string StringToSign(string timestamp, string scope, string canonicalRequestSha256Hex) =>
        $"{SigV4Names.Algorithm}\n{timestamp}\n{scope}\n{canonicalRequestSha256Hex}";

    /// <param name="service">Always <c>s3</c> on the server; other values only for the official SigV4 test suite.</param>
    public static string Scope(string date, string region, string service = SigV4Names.Service) =>
        $"{date}/{region}/{service}/{SigV4Names.Terminator}";

    /// <summary>Header value: trimmed, runs of whitespace collapsed to one space.</summary>
    private static void AppendTrimmed(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var span = value.AsSpan().Trim();
        var lastWasSpace = false;
        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }
    }

    /// <summary>
    /// RFC 3986 encoding of the UTF-8 bytes: unreserved characters stay, everything else is %XX (upper case). Also used
    /// for <c>encoding-type=url</c> listings.
    /// </summary>
    public static string Encode(string value, bool keepSlash)
    {
        var needsEncoding = false;
        foreach (var c in value)
        {
            if (!IsUnreserved(c) && !(keepSlash && c == '/'))
            {
                needsEncoding = true;
                break;
            }
        }

        if (!needsEncoding)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (b < 0x80 && (IsUnreserved(c) || (keepSlash && c == '/')))
            {
                builder.Append(c);
                continue;
            }

            builder.Append('%').Append(HexUpper[b >> 4]).Append(HexUpper[b & 0xF]);
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~';
}
