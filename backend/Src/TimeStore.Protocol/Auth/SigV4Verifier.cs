using System.Globalization;
using TimeStore.Cryptography;
using TimeStore.Primitives;
using TimeStore.Protocol.Routing;

namespace TimeStore.Protocol.Auth;

/// <summary>
/// Verifies AWS Signature Version 4 on an S3 request: the <c>Authorization</c> header or a presigned URL.
/// Checks, in order: mechanism, parameters, scope (service, region, date), time (skew or expiry), payload mode,
/// that every <c>x-amz-*</c> header is signed, the access key, then the signature (constant time).
/// The body itself (payload hash, chunk signatures) is verified later by the body stream (step 1c).
/// </summary>
public sealed class SigV4Verifier(
    ISecretKeyResolver secrets, SigningKeyCache signingKeys, TimeProvider clock, SigV4Options options)
{
    private const int CredentialParts = 5;

    public async ValueTask<Result<SigV4Principal>> VerifyAsync(ISigV4Request request, CancellationToken ct)
    {
        var queryStart = request.RawTarget.IndexOf('?');
        var rawPath = queryStart < 0 ? request.RawTarget : request.RawTarget[..queryStart];
        var rawQuery = queryStart < 0 ? string.Empty : request.RawTarget[(queryStart + 1)..];
        if (!S3Query.TryParse(rawQuery, out var query))
            return S3RequestErrors.InvalidUri;

        var authorization = request.GetHeader(SigV4Names.Authorization);
        var presigned = query.Contains(SigV4Names.QueryAlgorithm);
        if (authorization is not null && presigned)
            return S3AuthErrors.TwoMechanisms;
        if (authorization?.StartsWith(SigV4Names.SigV2Prefix, StringComparison.Ordinal) == true
            || query.Contains(SigV4Names.QuerySigV2Signature) || query.Contains(SigV4Names.QuerySigV2AccessKey))
            return S3AuthErrors.UnsupportedMechanism;
        if (authorization is null && !presigned)
            return S3AuthErrors.Anonymous;
        if (request.GetHeader(SigV4Names.SecurityToken) is not null || query.Contains(SigV4Names.QuerySecurityToken))
            return S3AuthErrors.InvalidToken;

        var parsed = presigned ? ParseQuery(query) : ParseHeader(authorization!, request);
        if (parsed.IsFailure)
            return parsed.Error;
        var auth = parsed.Value;

        if (CheckScope(auth) is { } scopeError)
            return scopeError;
        if (CheckTime(auth) is { } timeError)
            return timeError;
        if (!auth.SignedHeaders.Contains(SigV4Names.Host))
            return auth.Malformed("the host header must be signed.");
        if (HasUnsignedAmzHeaders(request, auth.SignedHeaders))
            return S3AuthErrors.UnsignedHeaders;

        var secret = await secrets.FindSecretKeyAsync(auth.AccessKeyId, ct);
        if (secret is null)
            return S3AuthErrors.InvalidAccessKey;
        var signingKey = signingKeys.GetOrDerive(auth.AccessKeyId, secret, auth.Date, options.Region);

        var canonicalQuery = SigV4Canonical.CanonicalQuery(rawQuery, presigned ? SigV4Names.QuerySignature : null);
        var canonicalUri = SigV4Canonical.CanonicalUri(rawPath);
        if (canonicalQuery is null || canonicalUri is null)
            return S3RequestErrors.InvalidUri;

        var scope = SigV4Canonical.Scope(auth.Date, options.Region);
        if (!SignatureMatches(request, auth, canonicalUri, canonicalQuery, scope, signingKey)
            && !(canonicalUri != rawPath // clients that signed the path exactly as they sent it
                 && SignatureMatches(request, auth, rawPath, canonicalQuery, scope, signingKey)))
            return S3AuthErrors.SignatureMismatch;

        return new SigV4Principal(
            auth.AccessKeyId, auth.SignedAt, auth.Timestamp, scope, auth.Signature, signingKey, auth.PayloadMode,
            auth.PayloadMode == PayloadMode.Sha256 ? auth.PayloadHash : null, presigned);
    }

    private static bool SignatureMatches(
        ISigV4Request request, ParsedAuth auth, string canonicalUri, string canonicalQuery, string scope, byte[] signingKey)
    {
        var canonical = SigV4Canonical.CanonicalRequest(
            request.Method, canonicalUri, canonicalQuery, request, auth.SignedHeaders, auth.PayloadHash);
        var stringToSign = SigV4Canonical.StringToSign(auth.Timestamp, scope, SigV4Signer.Sha256Hex(canonical));
        Span<byte> expected = stackalloc byte[Signing.Sha256Length];
        SigV4Signer.Sign(signingKey, stringToSign, expected);
        return SigV4Signer.Matches(auth.Signature, expected);
    }

    private AppError? CheckScope(ParsedAuth auth)
    {
        if (!string.Equals(auth.Service, SigV4Names.Service, StringComparison.Ordinal))
            return auth.Malformed($"the service '{auth.Service}' is wrong; expecting '{SigV4Names.Service}'.");
        if (!string.Equals(auth.Region, options.Region, StringComparison.Ordinal))
            return auth.Malformed($"the region '{auth.Region}' is wrong; expecting '{options.Region}'.");
        if (!string.Equals(auth.Terminator, SigV4Names.Terminator, StringComparison.Ordinal))
            return auth.Malformed($"the credential must end with '{SigV4Names.Terminator}'.");
        if (!string.Equals(auth.Date, auth.SignedAt.ToString(SigV4Names.DateFormat, CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return auth.Malformed("the credential date does not match the request date.");
        return null;
    }

    private AppError? CheckTime(ParsedAuth auth)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (auth.ExpiresSeconds is not { } expires)
            return (now - auth.SignedAt).Duration() > options.MaxClockSkew ? S3AuthErrors.TimeSkewed : null;

        if (auth.SignedAt - now > options.MaxClockSkew)
            return S3AuthErrors.NotYetValid;
        return now > auth.SignedAt.AddSeconds(expires) ? S3AuthErrors.Expired : null;
    }

    /// <summary>Every <c>x-amz-*</c> header must be signed (except the payload hash, which the signature covers).</summary>
    private static bool HasUnsignedAmzHeaders(ISigV4Request request, string[] signedHeaders)
    {
        foreach (var name in request.HeaderNames)
        {
            if (name.StartsWith(SigV4Names.AmzPrefix, StringComparison.Ordinal)
                && !string.Equals(name, SigV4Names.ContentSha256, StringComparison.Ordinal)
                && Array.IndexOf(signedHeaders, name) < 0)
                return true;
        }

        return false;
    }

    private static Result<ParsedAuth> ParseHeader(string authorization, ISigV4Request request)
    {
        static AppError Malformed(string reason) => S3AuthErrors.HeaderMalformed(reason);

        if (!authorization.StartsWith(SigV4Names.Algorithm + " ", StringComparison.Ordinal))
            return S3AuthErrors.UnsupportedMechanism;

        string? credential = null, signedHeaders = null, signature = null;
        foreach (var part in authorization[(SigV4Names.Algorithm.Length + 1)..].Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(SigV4Names.CredentialPart, StringComparison.Ordinal))
                credential = part[SigV4Names.CredentialPart.Length..];
            else if (part.StartsWith(SigV4Names.SignedHeadersPart, StringComparison.Ordinal))
                signedHeaders = part[SigV4Names.SignedHeadersPart.Length..];
            else if (part.StartsWith(SigV4Names.SignaturePart, StringComparison.Ordinal))
                signature = part[SigV4Names.SignaturePart.Length..];
        }

        if (credential is null || signedHeaders is null || signature is null)
            return Malformed("Credential, SignedHeaders and Signature are required.");

        string timestamp;
        DateTime signedAt;
        if (request.GetHeader(SigV4Names.AmzDate) is { } amzDate)
        {
            if (!TryParseTimestamp(amzDate, out signedAt))
                return Malformed("x-amz-date is not a valid ISO 8601 basic timestamp.");
            timestamp = amzDate;
        }
        else if (request.GetHeader(SigV4Names.Date) is { } httpDate
                 && DateTime.TryParseExact(httpDate, SigV4Names.HttpDateFormat, CultureInfo.InvariantCulture,
                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out signedAt))
        {
            timestamp = signedAt.ToString(SigV4Names.TimestampFormat, CultureInfo.InvariantCulture);
        }
        else
        {
            return Malformed("a valid x-amz-date or Date header is required.");
        }

        var payloadHash = request.GetHeader(SigV4Names.ContentSha256);
        if (payloadHash is null)
            return S3AuthErrors.MissingContentSha256;
        if (ToPayloadMode(payloadHash) is not { } mode)
            return S3AuthErrors.InvalidContentSha256;

        return Build(credential, signedHeaders, signature, timestamp, signedAt, payloadHash, mode, null, Malformed);
    }

    private static Result<ParsedAuth> ParseQuery(S3Query query)
    {
        static AppError Malformed(string reason) => S3AuthErrors.QueryMalformed(reason);

        if (query.Get(SigV4Names.QueryAlgorithm) != SigV4Names.Algorithm)
            return S3AuthErrors.UnsupportedMechanism;

        var credential = query.Get(SigV4Names.QueryCredential);
        var date = query.Get(SigV4Names.QueryDate);
        var expires = query.Get(SigV4Names.QueryExpires);
        var signedHeaders = query.Get(SigV4Names.QuerySignedHeaders);
        var signature = query.Get(SigV4Names.QuerySignature);
        if (credential is null || date is null || expires is null || signedHeaders is null || signature is null)
            return Malformed(
                "X-Amz-Credential, X-Amz-Date, X-Amz-Expires, X-Amz-SignedHeaders and X-Amz-Signature are required.");

        if (!TryParseTimestamp(date, out var signedAt))
            return Malformed("X-Amz-Date is not a valid ISO 8601 basic timestamp.");
        // A zero or negative lifetime is a URL that has already expired (403 in CheckTime), not a malformed one.
        if (!int.TryParse(expires, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var expiresSeconds)
            || expiresSeconds > SigV4Names.MaxPresignExpiresSeconds)
            return Malformed($"X-Amz-Expires must be at most {SigV4Names.MaxPresignExpiresSeconds} seconds.");

        var payloadHash = query.Get(SigV4Names.QueryContentSha256) ?? SigV4Names.UnsignedPayload;
        if (ToPayloadMode(payloadHash) is not { } mode)
            return S3AuthErrors.InvalidContentSha256;

        return Build(credential, signedHeaders, signature, date, signedAt, payloadHash, mode, expiresSeconds, Malformed);
    }

    private static Result<ParsedAuth> Build(
        string credential, string signedHeaders, string signature, string timestamp, DateTime signedAt,
        string payloadHash, PayloadMode mode, int? expires, Func<string, AppError> malformed)
    {
        var parts = credential.Split('/');
        if (parts.Length != CredentialParts || parts.Any(string.IsNullOrEmpty))
            return malformed("the credential must be <access-key>/<date>/<region>/s3/aws4_request.");

        var headers = signedHeaders.Split(';');
        if (headers.Any(name => name.Length == 0 || name.Any(char.IsAsciiLetterUpper)))
            return malformed("SignedHeaders must be a ';'-separated list of lower-case names.");

        return new ParsedAuth(
            parts[0], parts[1], parts[2], parts[3], parts[4], headers, signature, timestamp, signedAt, payloadHash,
            mode, expires, malformed);
    }

    private static bool TryParseTimestamp(string value, out DateTime utc) =>
        DateTime.TryParseExact(value, SigV4Names.TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    private static PayloadMode? ToPayloadMode(string value) => value switch
    {
        SigV4Names.UnsignedPayload => PayloadMode.Unsigned,
        SigV4Names.StreamingSigned => PayloadMode.StreamingSigned,
        SigV4Names.StreamingSignedTrailer => PayloadMode.StreamingSignedTrailer,
        SigV4Names.StreamingUnsignedTrailer => PayloadMode.StreamingUnsignedTrailer,
        _ when value.Length == SigV4Names.SignatureHexLength && value.All(char.IsAsciiHexDigitLower) => PayloadMode.Sha256,
        _ => null,
    };

    private sealed record ParsedAuth(
        string AccessKeyId,
        string Date,
        string Region,
        string Service,
        string Terminator,
        string[] SignedHeaders,
        string Signature,
        string Timestamp,
        DateTime SignedAt,
        string PayloadHash,
        PayloadMode PayloadMode,
        int? ExpiresSeconds,
        Func<string, AppError> Malformed);
}
