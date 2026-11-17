using System.Globalization;

namespace TimeStore.Protocol.Http;

public enum PreconditionOutcome
{
    /// <summary>Serve the object.</summary>
    Proceed,

    /// <summary>304 Not Modified (If-None-Match / If-Modified-Since).</summary>
    NotModified,

    /// <summary>412 PreconditionFailed (If-Match / If-Unmodified-Since).</summary>
    Failed,
}

/// <summary>
/// Conditional GET/HEAD in the order of RFC 9110 §13.2.2, as Amazon S3 applies it: If-Match, else If-Unmodified-Since
/// (412); then If-None-Match, else If-Modified-Since (304). Dates compare at whole seconds (HTTP date precision);
/// unparsable dates are ignored.
/// </summary>
public static class S3Preconditions
{
    private const string Wildcard = "*";
    private const string WeakPrefix = "W/";
    private const string HttpDateFormat = "r"; // RFC 1123

    public static PreconditionOutcome Evaluate(
        string? ifMatch, string? ifNoneMatch, string? ifModifiedSince, string? ifUnmodifiedSince,
        string etag, DateTime lastModifiedUtc)
    {
        var lastModified = TruncateToSeconds(lastModifiedUtc);

        if (ifMatch is not null)
        {
            if (!Matches(ifMatch, etag))
                return PreconditionOutcome.Failed;
        }
        else if (TryParseDate(ifUnmodifiedSince, out var unmodifiedSince) && lastModified > unmodifiedSince)
        {
            return PreconditionOutcome.Failed;
        }

        if (ifNoneMatch is not null)
            return Matches(ifNoneMatch, etag) ? PreconditionOutcome.NotModified : PreconditionOutcome.Proceed;

        return TryParseDate(ifModifiedSince, out var modifiedSince) && lastModified <= modifiedSince
            ? PreconditionOutcome.NotModified
            : PreconditionOutcome.Proceed;
    }

    /// <summary>True when the list contains <c>*</c> or the ETag (quotes and the weak prefix are ignored).</summary>
    private static bool Matches(string header, string etag)
    {
        foreach (var raw in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == Wildcard)
                return true;
            var candidate = raw.StartsWith(WeakPrefix, StringComparison.Ordinal) ? raw[WeakPrefix.Length..] : raw;
            if (candidate.Trim('"') == etag)
                return true;
        }

        return false;
    }

    private static bool TryParseDate(string? value, out DateTime utc) =>
        DateTime.TryParseExact(value, HttpDateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    private static DateTime TruncateToSeconds(DateTime utc) => utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
}
