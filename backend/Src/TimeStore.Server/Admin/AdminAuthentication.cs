using TimeStore.Application.Features.Auth;
using TimeStore.Domain.AccessKeys;

namespace TimeStore.Server.Admin;

/// <summary>
/// Guards the admin API (<c>/api/v1</c>, admin port only: the S3 port never gets here):
/// <list type="number">
/// <item>CSRF: a state-changing request must come from the same origin (<c>Origin</c> equal to this scheme and host,
/// or <c>Sec-Fetch-Site: same-origin</c>); the session cookie is also <c>SameSite=Strict</c>.</item>
/// <item>Session: every endpoint but sign-in needs a valid session cookie (401).</item>
/// <item>Role: <c>viewer</c> may only read (403 for anything else); <c>admin</c> may do everything.</item>
/// </list>
/// </summary>
internal static class AdminAuthentication
{
    public const string CookieName = "ts_session";
    private const string PrincipalItem = "TimeStore.AdminPrincipal";
    private const string SecFetchSite = "Sec-Fetch-Site";
    private const string SameOrigin = "same-origin";

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
