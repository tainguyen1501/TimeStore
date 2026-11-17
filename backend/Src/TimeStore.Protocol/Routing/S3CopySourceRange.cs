using System.Globalization;

namespace TimeStore.Protocol.Routing;

/// <summary>
/// <c>x-amz-copy-source-range</c> of UploadPartCopy: exactly <c>bytes=first-last</c> (inclusive, zero-based, first ≤
/// last). Anything else is invalid (Amazon S3 answers 400 InvalidArgument); whether it fits the source is checked
/// against the source's size later.
/// </summary>
public readonly record struct S3CopySourceRange(long First, long Last)
{
    private const string Prefix = "bytes=";

    public long Length => Last - First + 1;

    public static bool TryParse(string? text, out S3CopySourceRange range)
    {
        range = default;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var spec = text.AsSpan(Prefix.Length);
        var dash = spec.IndexOf('-');
        if (dash <= 0 || dash == spec.Length - 1)
            return false;
        if (!TryDigits(spec[..dash], out var first) || !TryDigits(spec[(dash + 1)..], out var last) || first > last)
            return false;
        range = new S3CopySourceRange(first, last);
        return true;
    }

    /// <summary>Plain decimal digits only (no sign, no spaces, no second range).</summary>
    private static bool TryDigits(ReadOnlySpan<char> digits, out long value)
    {
        value = 0;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
