using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using TimeStore.Application.Features.Auth;
using TimeStore.Domain.AccessKeys;
using TimeStore.Protocol.Auth;
using TimeStore.Server.S3;

namespace TimeStore.Server.Admin;

/// <summary>
/// Guards the admin API (<c>/api/v1</c>, admin port only: the S3 port never gets here):
/// <list type="number">
/// <item>CSRF: a state-changing request must come from the same origin (<c>Origin</c> equal to this scheme and host,
/// or <c>Sec-Fetch-Site: same-origin</c>); the session cookie is also <c>SameSite=Strict</c>.</item>
/// <item>Session: every endpoint but sign-in needs a valid session cookie (401).</item>
/// <item>Role: <c>viewer</c> may only read (403 for anything else); <c>admin</c> may do everything.</item>
/// </list>
/// The backup API (<see cref="AdminBackupApi.RoutePrefix"/>) is for programs instead: every request is signed with
/// SigV4 in the <c>Authorization</c> header (like an S3 request), cookies are refused, and there is no CSRF check (a
/// browser cannot sign); its endpoints check the role (<c>admin</c> or <c>backup</c>).
/// </summary>
internal static class AdminAuthentication
{
    public const string CookieName = "ts_session";
    private const string PrincipalItem = "TimeStore.AdminPrincipal";
    private const string SecFetchSite = "Sec-Fetch-Site";
    private const string SameOrigin = "same-origin";

    // The bodies of the backup API are small JSON documents; a signed body is read whole to check its hash.
    private const int MaxSignedBodyBytes = 64 * 1024;

    public static AdminPrincipal Principal(this HttpContext context) =>
        context.Items[PrincipalItem] as AdminPrincipal
        ?? throw new InvalidOperationException("The admin authentication middleware did not run.");

    public static void UseAdminAuthentication(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!path.StartsWithSegments(AdminRoutes.Prefix))
            {
                await next(context);
                return;
            }

            if (path.StartsWithSegments(AdminBackupApi.RoutePrefix))
            {
                await AuthenticateSignedAsync(context, next);
                return;
            }

            var method = context.Request.Method;
            var reads = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
            if (!reads && !IsSameOrigin(context.Request))
            {
                await ApiResults.Error(AuthErrors.CrossSite, context).ExecuteAsync(context);
                return;
            }

            if (path.Equals(AdminRoutes.Prefix + AdminRoutes.Auth + AdminRoutes.Login, StringComparison.Ordinal))
            {
                await next(context);
                return;
            }

            var auth = context.RequestServices.GetRequiredService<IAuthService>();
            var principal = context.Request.Cookies.TryGetValue(CookieName, out var token)
                ? await auth.AuthenticateAsync(token, context.RequestAborted)
                : null;
            if (principal is null)
            {
                await ApiResults.Error(AuthErrors.Unauthenticated, context).ExecuteAsync(context);
                return;
            }

            if (!reads && principal.Role != AccessKeyRoles.Admin
                && !path.Equals(AdminRoutes.Prefix + AdminRoutes.Auth + AdminRoutes.Logout, StringComparison.Ordinal))
            {
                await ApiResults.Error(AuthErrors.Forbidden, context).ExecuteAsync(context);
                return;
            }

            context.Items[PrincipalItem] = principal;
            await next(context);
        });

    /// <summary>
    /// SigV4 in the <c>Authorization</c> header, by an active key. A request without one (a cookie, a presigned URL)
    /// gets <c>AUTH_SIGNATURE_REQUIRED</c>; a bad signature or an unknown key the S3 authentication errors (403).
    /// </summary>
    private static async Task AuthenticateSignedAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (request.Headers.Authorization.Count == 0)
        {
            await ApiResults.Error(AuthErrors.SignatureRequired, context).ExecuteAsync(context);
            return;
        }

        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? $"{request.Path}{request.QueryString}";
        var verifier = context.RequestServices.GetRequiredService<SigV4Verifier>();
        var verified = await verifier.VerifyAsync(new HttpSigV4Request(request, rawTarget), context.RequestAborted);
        if (verified.IsFailure)
        {
            await ApiResults.Error(verified.Error, context).ExecuteAsync(context);
            return;
        }

        if (!await BodyMatchesAsync(request, verified.Value, context.RequestAborted))
        {
            await ApiResults.Error(AuthErrors.PayloadMismatch, context).ExecuteAsync(context);
            return;
        }

        var auth = context.RequestServices.GetRequiredService<IAuthService>();
        var principal = await auth.AuthenticateSignedAsync(verified.Value.AccessKeyId, context.RequestAborted);
        if (principal is null)
        {
            await ApiResults.Error(AuthErrors.Forbidden, context).ExecuteAsync(context);
            return;
        }

        context.Items[PrincipalItem] = principal;
        await next(context);
    }

    /// <summary>
    /// <c>UNSIGNED-PAYLOAD</c> passes; a signed hash is checked against the whole body (read here, then replayed);
    /// streaming (<c>aws-chunked</c>) payloads are refused: no endpoint here takes a large body.
    /// </summary>
    private static async ValueTask<bool> BodyMatchesAsync(HttpRequest request, SigV4Principal principal, CancellationToken ct)
    {
        if (principal.PayloadMode == PayloadMode.Unsigned)
            return true;
        if (principal.PayloadMode != PayloadMode.Sha256 || request.ContentLength > MaxSignedBodyBytes)
            return false;

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxSignedBodyBytes)
                return false;
            buffer.Write(chunk, 0, read);
        }

        var body = buffer.ToArray();
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(body)), principal.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        request.Body = new MemoryStream(body, writable: false);
        return true;
    }

    /// <summary>Same origin when the browser says so, or when <c>Origin</c> names this scheme and host.</summary>
    private static bool IsSameOrigin(HttpRequest request)
    {
        if (string.Equals(request.Headers[SecFetchSite].ToString(), SameOrigin, StringComparison.Ordinal))
            return true;
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetSessionCookie(this HttpContext context, string token, DateTime expiresAt, bool forceSecure) =>
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = forceSecure || context.Request.IsHttps,
            Path = "/",
            Expires = new DateTimeOffset(expiresAt, TimeSpan.Zero),
            IsEssential = true,
        });

    public static void ClearSessionCookie(this HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
