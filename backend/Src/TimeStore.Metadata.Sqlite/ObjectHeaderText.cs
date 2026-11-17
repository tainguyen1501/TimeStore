using System.Text;
using TimeStore.Domain.Objects;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// Storage format of object headers and user metadata: <c>name:value\n</c> per entry. Header names cannot contain ':'
/// and HTTP header values cannot contain CR, LF or NUL, so the format is unambiguous. Hand-written (AOT, no
/// reflection serializer); null for an empty list.
/// </summary>
internal static class ObjectHeaderText
{
    private const char Separator = ':';
    private const char Terminator = '\n';

    public static string? Encode(IReadOnlyList<ObjectHeader> headers)
    {
        if (headers.Count == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var header in headers)
            builder.Append(header.Name).Append(Separator).Append(header.Value).Append(Terminator);
        return builder.ToString();
    }

    public static IReadOnlyList<ObjectHeader> Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var headers = new List<ObjectHeader>();
        foreach (var range in text.AsSpan().Split(Terminator))
        {
            var line = text.AsSpan()[range];
            if (line.IsEmpty)
                continue;
            var separator = line.IndexOf(Separator);
            headers.Add(new ObjectHeader(line[..separator].ToString(), line[(separator + 1)..].ToString()));
        }

        return headers;
    }
}
