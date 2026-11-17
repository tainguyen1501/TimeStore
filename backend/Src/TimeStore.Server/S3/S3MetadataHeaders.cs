using System.Text;
using TimeStore.Protocol.Routing;

namespace TimeStore.Server.S3;

/// <summary>
/// How user metadata (<c>x-amz-meta-*</c>) values with non-ASCII text go back to the client. Kestrel writes response
/// headers as ASCII and fails the whole response (500) on anything else, so:
/// <list type="bullet">
/// <item>text within Latin-1 is written as Latin-1 bytes (the bytes Amazon S3 answers with: HTTP clients decode
/// headers as ISO-8859-1, so <c>é</c> arrives as <c>é</c>);</item>
/// <item>text beyond Latin-1 (e.g. Vietnamese <c>ả</c>) is written RFC 2047 encoded (<c>=?UTF-8?B?…?=</c>), as the
/// Amazon S3 user guide describes for non-US-ASCII metadata, so nothing is lost or replaced.</item>
/// </list>
/// Every other header keeps Kestrel's default.
/// </summary>
internal static class S3MetadataHeaders
{
    private const string Rfc2047Prefix = "=?UTF-8?B?";
    private const string Rfc2047Suffix = "?=";

    public static Encoding? Encoding(string headerName) =>
        headerName.StartsWith(S3HeaderNames.UserMetadataPrefix, StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.Latin1
            : null;

    /// <summary>The value to write: unchanged when Latin-1 can carry it, RFC 2047 B-encoded otherwise.</summary>
    public static string Value(string value)
    {
        foreach (var c in value)
        {
            if (c > '\u00FF')
                return Rfc2047Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)) + Rfc2047Suffix;
        }

        return value;
    }
}
