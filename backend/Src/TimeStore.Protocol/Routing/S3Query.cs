using System.Diagnostics.CodeAnalysis;

namespace TimeStore.Protocol.Routing;

/// <summary>
/// Decoded query string. Names and values are percent-decoded ('+' is a space); a name without '=' has the value "".
/// The first occurrence of a name wins.
/// </summary>
public sealed class S3Query
{
    private static readonly S3Query EmptyQuery = new([]);
    private readonly List<KeyValuePair<string, string>> _items;

    private S3Query(List<KeyValuePair<string, string>> items) => _items = items;

    public int Count => _items.Count;

    public IEnumerable<string> Names => _items.Select(item => item.Key);

    /// <summary>Parses the part after '?' (without it). False when an escape or UTF-8 sequence is malformed.</summary>
    public static bool TryParse(ReadOnlySpan<char> raw, [NotNullWhen(true)] out S3Query? query)
    {
        if (raw.IsEmpty)
        {
            query = EmptyQuery;
            return true;
        }

        var items = new List<KeyValuePair<string, string>>();
        foreach (var range in raw.Split('&'))
        {
            var pair = raw[range];
            if (pair.IsEmpty)
                continue;

            var equals = pair.IndexOf('=');
            var rawName = equals < 0 ? pair : pair[..equals];
            var rawValue = equals < 0 ? ReadOnlySpan<char>.Empty : pair[(equals + 1)..];
            if (!S3PercentDecoder.TryDecode(rawName, plusAsSpace: true, out var name)
                || !S3PercentDecoder.TryDecode(rawValue, plusAsSpace: true, out var value))
            {
                query = null;
                return false;
            }

            items.Add(new(name, value));
        }

        query = new S3Query(items);
        return true;
    }

    public bool Contains(string name) => IndexOf(name) >= 0;

    /// <summary>The value, "" for a bare flag, or null when absent.</summary>
    public string? Get(string name) => IndexOf(name) is var index and >= 0 ? _items[index].Value : null;

    private int IndexOf(string name)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Key, name, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
