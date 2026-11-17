using TimeStore.Application.Abstractions;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:S3</c>.</summary>
public sealed class S3Options
{
    public const string Section = ServerOptions.Section + ":S3";
    public const string DefaultRegion = "us-east-1";
    public const int DefaultMaxClockSkewSeconds = 900;
    private const int MaxClockSkewLimitSeconds = 3600;

    /// <summary>The region clients sign with and <c>GetBucketLocation</c> reports.</summary>
    public string Region { get; set; } = DefaultRegion;

    /// <summary>
    /// Domains whose sub-domains name buckets (virtual-host style, <c>bucket.s3.example.com</c>). Empty: path style
    /// only. Path style always works, also on these domains, IP addresses and unknown hosts.
    /// </summary>
    public string[] Domains { get; set; } = [];

    /// <summary>
    /// Root access key id: the break-glass key. Not stored in the database, never listed, and it cannot be disabled or
    /// deleted through the admin API. Required; no built-in default.
    /// </summary>
    public string RootAccessKey { get; set; } = string.Empty;

    /// <summary>Root secret key. Required; never logged. Set it through environment variables or a secret store.</summary>
    public string RootSecretKey { get; set; } = string.Empty;

    /// <summary>Maximum distance between a signed request's time and the server clock.</summary>
    public int MaxClockSkewSeconds { get; set; } = DefaultMaxClockSkewSeconds;

    /// <summary>
    /// How clients outside reach the S3 port (scheme, host, port; no path), e.g. <c>https://s3.example.com</c> behind a
    /// proxy. Used for the links the admin UI shows and shares. Empty: the admin request's host on <c>S3Port</c>.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>The parsed <see cref="PublicUrl"/>, or null when it is not set.</summary>
    public Uri? PublicEndpoint => string.IsNullOrWhiteSpace(PublicUrl) ? null : new Uri(PublicUrl, UriKind.Absolute);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Region))
            return $"{Section}:{nameof(Region)} is required.";
        if (RootAccessKey.Length is < AccessCredential.AccessKeyMinLength or > AccessCredential.AccessKeyMaxLength
            || !RootAccessKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return $"{Section}:{nameof(RootAccessKey)} is required: {AccessCredential.AccessKeyMinLength}-{AccessCredential.AccessKeyMaxLength} letters, digits, '-', '_' or '.'.";
        if (RootSecretKey.Length is < AccessCredential.SecretKeyMinLength or > AccessCredential.SecretKeyMaxLength)
            return $"{Section}:{nameof(RootSecretKey)} is required: {AccessCredential.SecretKeyMinLength}-{AccessCredential.SecretKeyMaxLength} characters.";
        if (MaxClockSkewSeconds is < 1 or > MaxClockSkewLimitSeconds)
            return $"{Section}:{nameof(MaxClockSkewSeconds)} must be 1-{MaxClockSkewLimitSeconds}.";
        if (!string.IsNullOrWhiteSpace(PublicUrl)
            && (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var publicUrl)
                || publicUrl.Scheme is not ("http" or "https")
                || publicUrl.AbsolutePath != "/" || publicUrl.Query.Length > 0 || publicUrl.UserInfo.Length > 0))
            return $"{Section}:{nameof(PublicUrl)} must be an http(s) URL without path, query or user info.";
        foreach (var domain in Domains)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain.AsSpan().IndexOfAny("/:@ ") >= 0)
                return $"{Section}:{nameof(Domains)} must contain host names only (no scheme, port or path): '{domain}'.";
        }

        return null;
    }
}
