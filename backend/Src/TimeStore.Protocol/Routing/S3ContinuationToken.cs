using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace TimeStore.Protocol.Routing;

/// <summary>
/// ListObjectsV2 continuation tokens: always base64url (no padding) of the UTF-8 last entry (key or common prefix).
/// Opaque to clients, as S3 defines them, and always encoded, so there is no "raw key or encoded key?" ambiguity and
/// any key travels safely in XML.
/// </summary>
public static class S3ContinuationToken
{
    public static string Encode(string lastEntry) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(lastEntry));

    /// <summary>False for anything this server did not produce (not base64url, not UTF-8, empty).</summary>
    public static bool TryDecode(string token, [NotNullWhen(true)] out string? lastEntry)
    {
        lastEntry = null;
        if (string.IsNullOrEmpty(token) || !Base64Url.IsValid(token, out var length) || length == 0)
            return false;

        var bytes = new byte[length];
        if (Base64Url.DecodeFromChars(token, bytes) != length || !Utf8.IsValid(bytes))
            return false;
        lastEntry = Encoding.UTF8.GetString(bytes);
        return true;
    }
}
