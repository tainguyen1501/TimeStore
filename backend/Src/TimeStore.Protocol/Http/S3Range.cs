using System.Globalization;

namespace TimeStore.Protocol.Http;

/// <summary>A single byte range to send.</summary>
public readonly record struct ByteRange(long Start, long Length)
{
    public long End => Start + Length - 1;

    /// <summary><c>Content-Range</c> value: <c>bytes start-end/size</c>.</summary>
    public string ContentRange(long size) => $"bytes {Start}-{End}/{size}";
}

public enum RangeKind
{
    /// <summary>No Range header, or one Amazon S3 ignores (malformed, several ranges): send the whole object.</summary>
    Whole,

    /// <summary>A satisfiable single range: answer 206.</summary>
    Partial,

    /// <summary>Syntactically valid but outside the object: answer 416 InvalidRange.</summary>
    Unsatisfiable,
}

public readonly record struct RangeRequest(RangeKind Kind, ByteRange Range);

/// <summary>
/// The <c>Range</c> header as Amazon S3 interprets it: one range of <c>bytes=a-b</c>, <c>bytes=a-</c> or
/// <c>bytes=-n</c>; several ranges are not supported (whole object), malformed values are ignored.
/// </summary>
public static class S3Range
{
    private const string Unit = "bytes=";

    public static RangeRequest Parse(string? header, long size)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Unit, StringComparison.OrdinalIgnoreCase))
            return Whole;

        var spec = header.AsSpan(Unit.Length).Trim();
        if (spec.Contains(','))
            return Whole;
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return Whole;

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.IsEmpty)
        {
            // Suffix: the last n bytes.
            if (!TryParse(last, out var suffix))
                return Whole;
            if (suffix == 0 || size == 0)
                return Unsatisfiable;
            var length = Math.Min(suffix, size);
            return Partial(size - length, length);
        }

        if (!TryParse(first, out var start))
            return Whole;
        long end;
        if (last.IsEmpty)
            end = size - 1;
        else if (!TryParse(last, out end) || end < start)
            return Whole;

        if (start >= size)
            return Unsatisfiable;
        end = Math.Min(end, size - 1);
        return Partial(start, end - start + 1);
    }

    private static RangeRequest Whole => new(RangeKind.Whole, default);

    private static RangeRequest Unsatisfiable => new(RangeKind.Unsatisfiable, default);

    private static RangeRequest Partial(long start, long length) => new(RangeKind.Partial, new ByteRange(start, length));

    private static bool TryParse(ReadOnlySpan<char> digits, out long value) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
