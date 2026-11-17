namespace TimeStore.Domain.Buckets;

/// <summary>Amazon S3 bucket naming rules (general purpose buckets).</summary>
public static class BucketNames
{
    public const string ReservedPrefix = "xn--";
    public const string ReservedSuffix = "-s3alias";
    private const char Dot = '.';
    private const char Hyphen = '-';
    private const int IpV4Dots = 3;

    public static bool IsValid(ReadOnlySpan<char> name)
    {
        if (name.Length is < Bucket.NameMinLength or > Bucket.NameMaxLength)
            return false;
        if (!IsLowerLetterOrDigit(name[0]) || !IsLowerLetterOrDigit(name[^1]))
            return false;

        var dots = 0;
        var onlyDigitsAndDots = true;
        for (var i = 1; i < name.Length - 1; i++)
        {
            var c = name[i];
            if (c == Dot)
            {
                if (name[i - 1] == Dot)
                    return false; // no adjacent periods
                dots++;
                continue;
            }

            if (!IsLowerLetterOrDigit(c) && c != Hyphen)
                return false;
            onlyDigitsAndDots &= char.IsAsciiDigit(c);
        }

        if (dots == IpV4Dots && onlyDigitsAndDots)
            return false; // looks like an IPv4 address

        return !name.StartsWith(ReservedPrefix, StringComparison.Ordinal)
            && !name.EndsWith(ReservedSuffix, StringComparison.Ordinal);
    }

    private static bool IsLowerLetterOrDigit(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
}
