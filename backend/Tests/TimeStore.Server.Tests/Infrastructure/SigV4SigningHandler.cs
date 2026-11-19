using System.Globalization;
using System.Security.Cryptography;
using TimeStore.Protocol.Auth;

namespace TimeStore.Server.Tests.Infrastructure;

/// <summary>
/// Signs every request with AWS Signature Version 4 in the <c>Authorization</c> header, the way <c>curl --aws-sigv4</c>
/// and the AWS SDKs do (signed headers <c>host</c>, <c>x-amz-content-sha256</c>, <c>x-amz-date</c>; the body's
/// SHA-256), for the APIs that take signed requests instead of a sign-in cookie (the backup API).
/// </summary>
/// <param name="inner">What sends the signed request (a plain HTTP handler by default; a test can tamper in between).</param>
public sealed class SigV4SigningHandler(string accessKey, string secretKey, string region, HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new HttpClientHandler())
{
    private const string DateFormat = "yyyyMMdd";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string SignedHeaders = "host;x-amz-content-sha256;x-amz-date";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Content is not null)
        {
            // The original content may be a one-time stream (JsonContent): send the bytes that were hashed.
            var copy = new ByteArrayContent(body);
            foreach (var header in request.Content.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = copy;
        }

        var now = DateTime.UtcNow;
        var date = now.ToString(DateFormat, CultureInfo.InvariantCulture);
        var timestamp = now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(body));
        var uri = request.RequestUri!;
        request.Headers.Host = uri.Authority;
        request.Headers.Add(SigV4Names.ContentSha256, payloadHash);
        request.Headers.Add(SigV4Names.AmzDate, timestamp);

        var canonicalQuery = SigV4Canonical.CanonicalQuery(uri.Query.TrimStart('?'), exclude: null)!;
        var canonicalRequest = string.Join('\n',
            request.Method.Method, SigV4Canonical.CanonicalUri(uri.AbsolutePath), canonicalQuery,
            $"host:{uri.Authority}", $"{SigV4Names.ContentSha256}:{payloadHash}", $"{SigV4Names.AmzDate}:{timestamp}", string.Empty,
            SignedHeaders, payloadHash);
        var scope = SigV4Canonical.Scope(date, region);
        var stringToSign = SigV4Canonical.StringToSign(timestamp, scope, SigV4Signer.Sha256Hex(canonicalRequest));
        var signature = new byte[32];
        SigV4Signer.Sign(SigV4Signer.DeriveSigningKey(secretKey, date, region), stringToSign, signature);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={SignedHeaders}, Signature={Convert.ToHexStringLower(signature)}");

        return await base.SendAsync(request, cancellationToken);
    }
}
