using Microsoft.AspNetCore.Http.Features;

namespace TimeStore.Server;

/// <summary>
/// Kestrel refuses every request body over 30,000,000 bytes (<c>KestrelServerLimits.MaxRequestBodySize</c>), which
/// suits JSON and XML but not uploads, which S3 allows up to 5 GiB per request. Uploads raise the limit for their
/// own request, before anything reads the body (the feature is read-only after that). The S3 port is a middleware
/// branch that never reaches endpoint routing, so <c>[RequestSizeLimit]</c> endpoint metadata would not apply there.
/// </summary>
internal static class RequestBodyLimit
{
    /// <summary>Lets this request's body be up to <paramref name="maxBytes"/> bytes; null removes the limit.</summary>
    public static void Allow(HttpContext http, long? maxBytes)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maxBytes;
    }
}
