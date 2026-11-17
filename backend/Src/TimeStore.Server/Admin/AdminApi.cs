using TimeStore.Contracts.Lifecycle;
using TimeStore.Contracts.Policies;
using TimeStore.Application.Features.Policies;
using TimeStore.Application.Features.Lifecycle;
using System.Globalization;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using TimeStore.Application.Features.AccessKeys;
using TimeStore.Application.Features.Auth;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Maintenance;
using TimeStore.Contracts.Maintenance;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Auth;
using TimeStore.Contracts.Buckets;
using TimeStore.Contracts.Common;
using TimeStore.Primitives;

namespace TimeStore.Server.Admin;

internal static class AdminRoutes
{
    public const string Prefix = "/api/v1";
    public const string Buckets = "/buckets";
    public const string Root = "/";
    public const string ByName = "/{name}";
    public const string Versioning = "/{name}/versioning";
    public const string Lifecycle = "/{name}/lifecycle";
    public const string Policy = "/{name}/policy";
    public const string Public = "/{name}/public";
    public const string Scope = "/{id}/scope";
    public const string RunLifecycle = "/lifecycle";
    public const string AccessKeys = "/access-keys";
    public const string ById = "/{id}";
    public const string Disable = "/{id}/disable";
    public const string Enable = "/{id}/enable";
    public const string Maintenance = "/maintenance";
    public const string BlobSweep = "/blob-sweep";
    public const string Role = "/{id}/role";
    public const string Auth = "/auth";
    public const string Login = "/login";
    public const string Logout = "/logout";
    public const string Me = "/me";
    public const string OpenApiPrefix = "/openapi";
}

internal static class HealthRoutes
{
    public const string Prefix = "/health";
    public const string Live = "/health/live";
}

/// <summary>Admin API for the React UI. Endpoints only call services and map results.</summary>
internal static class AdminApi
{
    private const string NoStore = "no-store";

    public static void MapHealthEndpoints(this WebApplication app) =>
        app.MapGet(HealthRoutes.Live, ([FromServices] ServerOptions options) =>
                TypedResults.Ok(new HealthResponse(HealthResponse.Healthy, options.NodeId)))
            .ShortCircuit();

    /// <summary>
    /// Admin endpoints. They are reachable only on the admin port because <c>S3Middleware</c> answers every request
    /// on the S3 port (by local port, not Host header, so port mapping and reverse proxies do not matter).
    /// </summary>
    public static void MapAdminApi(this WebApplication app)
    {
        var auth = app.MapGroup(AdminRoutes.Prefix + AdminRoutes.Auth);
        auth.MapPost(AdminRoutes.Login, LoginAsync).Errors(400, 401, 403, 429);
        auth.MapPost(AdminRoutes.Logout, LogoutAsync).Errors(401, 403);
        auth.MapGet(AdminRoutes.Me, (HttpContext http) =>
        {
            var principal = http.Principal();
            return TypedResults.Ok(new SessionResponse(principal.AccessKeyId, principal.Role, principal.ExpiresAt));
        }).Errors(401);

        var buckets = app.MapGroup(AdminRoutes.Prefix + AdminRoutes.Buckets);
        buckets.MapGet(AdminRoutes.Root, ListBucketsAsync).Errors(401);
        buckets.MapPost(AdminRoutes.Root, CreateBucketAsync).Errors(400, 401, 403, 409);
        buckets.MapDelete(AdminRoutes.ByName, DeleteBucketAsync).Errors(401, 403, 404, 409);
        buckets.MapPut(AdminRoutes.Versioning, SetVersioningAsync).Errors(400, 401, 403, 404);
        buckets.MapGet(AdminRoutes.Lifecycle, GetLifecycleAsync).Errors(401, 404);
        buckets.MapPut(AdminRoutes.Lifecycle, SetLifecycleAsync).Errors(400, 401, 403, 404);
        buckets.MapDelete(AdminRoutes.Lifecycle, DeleteLifecycleAsync).Errors(401, 403, 404);
        buckets.MapGet(AdminRoutes.Policy, GetPolicyAsync).Errors(401, 404);
        buckets.MapPut(AdminRoutes.Policy, SetPolicyAsync).Errors(400, 401, 403, 404);
        buckets.MapDelete(AdminRoutes.Policy, DeletePolicyAsync).Errors(401, 403, 404);
        buckets.MapGet(AdminRoutes.Public, GetPublicAsync).Errors(401, 404);
        buckets.MapPut(AdminRoutes.Public, SetPublicAsync).Errors(400, 401, 403, 404, 409);

        var keys = app.MapGroup(AdminRoutes.Prefix + AdminRoutes.AccessKeys);
        keys.MapGet(AdminRoutes.Root, ListAccessKeysAsync).Errors(401);
        keys.MapPost(AdminRoutes.Root, CreateAccessKeyAsync).Errors(400, 401, 403);
        keys.MapPost(AdminRoutes.Disable, DisableAccessKeyAsync).Errors(401, 403, 404);
        keys.MapPost(AdminRoutes.Enable, EnableAccessKeyAsync).Errors(401, 403, 404);
        keys.MapPost(AdminRoutes.Role, SetAccessKeyRoleAsync).Errors(400, 401, 403, 404);
        keys.MapPost(AdminRoutes.Scope, SetAccessKeyScopeAsync).Errors(400, 401, 403, 404);
        keys.MapDelete(AdminRoutes.ById, DeleteAccessKeyAsync).Errors(401, 403, 404);

        var maintenance = app.MapGroup(AdminRoutes.Prefix + AdminRoutes.Maintenance);
        maintenance.MapPost(AdminRoutes.BlobSweep, SweepBlobsAsync).Errors(401, 403, 409);
        maintenance.MapPost(AdminRoutes.RunLifecycle, RunLifecycleAsync).Errors(401, 403);
    }

    /// <summary>
    /// Declares the error statuses of an endpoint for the OpenAPI document: <c>JsonHttpResult</c> has no static status,
    /// so without this the generated client would not know the error body. Every listed status answers
    /// <see cref="ErrorResponse"/> (see docs/api/admin.md).
    /// </summary>
    internal static RouteHandlerBuilder Errors(this RouteHandlerBuilder endpoint, params int[] statuses)
    {
        foreach (var status in statuses)
            endpoint.Produces<ErrorResponse>(status);
        return endpoint;
    }

    private static async Task<Results<Ok<BlobSweepResponse>, JsonHttpResult<ErrorResponse>>> SweepBlobsAsync(
        IBlobSweepService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.SweepAsync(ct), http);

    /// <summary>Sign-in with an access key; the session token travels only in the HttpOnly cookie.</summary>
    private static async Task<Results<Ok<SessionResponse>, JsonHttpResult<ErrorResponse>>> LoginAsync(
        LoginRequest request, IAuthService auth, AdminSettings settings, HttpContext http, CancellationToken ct)
    {
        var client = http.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var result = await auth.LoginAsync(request, client, ct);
        http.Response.Headers.CacheControl = NoStore;
        if (result.IsFailure)
        {
            if (auth.RetryAfter(request.AccessKeyId, client) is { } wait)
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return ApiResults.Error(result.Error, http);
        }

        http.SetSessionCookie(result.Value.Token, result.Value.Session.ExpiresAt, settings.SecureCookies);
        return TypedResults.Ok(result.Value.Session);
    }

    private static async Task<NoContent> LogoutAsync(IAuthService auth, HttpContext http, CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(AdminAuthentication.CookieName, out var token))
            await auth.LogoutAsync(token, ct);
        http.ClearSessionCookie();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<AccessKeyResponse>, JsonHttpResult<ErrorResponse>>> SetAccessKeyRoleAsync(
        string id, SetAccessKeyRoleRequest request, IAccessKeyService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.SetRoleAsync(id, request, ct), http);

    private static async Task<Results<Ok<AccessKeyResponse>, JsonHttpResult<ErrorResponse>>> SetAccessKeyScopeAsync(
        string id, SetAccessKeyScopeRequest request, IAccessKeyService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.SetScopeAsync(id, request, ct), http);

    private static async Task<Ok<IReadOnlyList<AccessKeyResponse>>> ListAccessKeysAsync(
        IAccessKeyService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListAsync(ct));

    private static async Task<Results<Created<CreatedAccessKeyResponse>, JsonHttpResult<ErrorResponse>>> CreateAccessKeyAsync(
        CreateAccessKeyRequest request, IAccessKeyService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        if (!result.IsSuccess)
            return ApiResults.Error(result.Error, http);

        // The only response that carries a secret: keep proxies and browsers from storing it.
        http.Response.Headers.CacheControl = NoStore;
        return TypedResults.Created($"{AdminRoutes.Prefix}{AdminRoutes.AccessKeys}/{result.Value.AccessKeyId}", result.Value);
    }

    private static async Task<Results<Ok<AccessKeyResponse>, JsonHttpResult<ErrorResponse>>> DisableAccessKeyAsync(
        string id, IAccessKeyService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.DisableAsync(id, ct), http);

    private static async Task<Results<Ok<AccessKeyResponse>, JsonHttpResult<ErrorResponse>>> EnableAccessKeyAsync(
        string id, IAccessKeyService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.EnableAsync(id, ct), http);

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeleteAccessKeyAsync(
        string id, IAccessKeyService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.DeleteAsync(id, ct);
        return result.IsSuccess ? TypedResults.NoContent() : ApiResults.Error(result.Error, http);
    }

    private static async Task<Ok<IReadOnlyList<BucketResponse>>> ListBucketsAsync(
        IBucketService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListAsync(ct));

    private static async Task<Results<Created<BucketResponse>, JsonHttpResult<ErrorResponse>>> CreateBucketAsync(
        CreateBucketRequest request, IBucketService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.IsSuccess
            ? TypedResults.Created($"{AdminRoutes.Prefix}{AdminRoutes.Buckets}/{result.Value.Name}", result.Value)
            : ApiResults.Error(result.Error, http);
    }

    private static async Task<Results<Ok<BucketResponse>, JsonHttpResult<ErrorResponse>>> SetVersioningAsync(
        string name, SetBucketVersioningRequest request, IBucketService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.SetVersioningAsync(name, request, ct), http);

    /// <summary>The bucket's rules; a bucket without a configuration answers <c>LIFECYCLE_NOT_FOUND</c> (404).</summary>
    private static async Task<Results<Ok<LifecycleConfigurationDto>, JsonHttpResult<ErrorResponse>>> GetLifecycleAsync(
        string name, ILifecycleService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.GetConfigurationAsync(name, ct), http);

    private static async Task<Results<Ok<LifecycleConfigurationDto>, JsonHttpResult<ErrorResponse>>> SetLifecycleAsync(
        string name, LifecycleConfigurationDto request, ILifecycleService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.SetConfigurationAsync(name, request, ct), http);

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeleteLifecycleAsync(
        string name, ILifecycleService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.DeleteAsync(name, ct);
        return result.IsSuccess ? TypedResults.NoContent() : ApiResults.Error(result.Error, http);
    }

    /// <summary>The bucket's policy document; a bucket without one answers <c>POLICY_NOT_FOUND</c> (404).</summary>
    private static async Task<Results<Ok<BucketPolicyResponse>, JsonHttpResult<ErrorResponse>>> GetPolicyAsync(
        string name, IBucketPolicyService service, HttpContext http, CancellationToken ct)
    {
        var document = await service.GetAsync(name, ct);
        if (document.IsFailure)
            return ApiResults.Error(document.Error, http);
        var isPublic = await service.IsPublicAsync(name, ct);
        return TypedResults.Ok(new BucketPolicyResponse(document.Value, isPublic.IsSuccess && isPublic.Value));
    }

    private static async Task<Results<Ok<BucketPolicyResponse>, JsonHttpResult<ErrorResponse>>> SetPolicyAsync(
        string name, SetBucketPolicyRequest request, IBucketPolicyService service, HttpContext http, CancellationToken ct)
    {
        var set = await service.SetAsync(name, request.Document ?? string.Empty, ct);
        return set.IsFailure ? ApiResults.Error(set.Error, http) : await GetPolicyAsync(name, service, http, ct);
    }

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeletePolicyAsync(
        string name, IBucketPolicyService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.DeleteAsync(name, ct);
        return result.IsSuccess ? TypedResults.NoContent() : ApiResults.Error(result.Error, http);
    }

    /// <summary>The bucket and folders made public with "Make public" (step 7g).</summary>
    private static async Task<Results<Ok<PublicAccessResponse>, JsonHttpResult<ErrorResponse>>> GetPublicAsync(
        string name, IBucketPolicyService service, HttpContext http, CancellationToken ct)
    {
        var prefixes = await service.GetPublicPrefixesAsync(name, ct);
        return prefixes.IsFailure ? ApiResults.Error(prefixes.Error, http) : TypedResults.Ok(new PublicAccessResponse(prefixes.Value));
    }

    private static async Task<Results<Ok<PublicAccessResponse>, JsonHttpResult<ErrorResponse>>> SetPublicAsync(
        string name, SetPublicAccessRequest request, IBucketPolicyService service, HttpContext http, CancellationToken ct)
    {
        var prefixes = await service.SetPublicAsync(name, request.Prefix ?? string.Empty, request.IsPublic, ct);
        return prefixes.IsFailure ? ApiResults.Error(prefixes.Error, http) : TypedResults.Ok(new PublicAccessResponse(prefixes.Value));
    }

    private static async Task<Ok<LifecycleRunResponse>> RunLifecycleAsync(ILifecycleRunner runner, CancellationToken ct) =>
        TypedResults.Ok(await runner.RunAsync(ct));

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeleteBucketAsync(
        string name, IBucketService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.DeleteAsync(name, ct);
        return result.IsSuccess ? TypedResults.NoContent() : ApiResults.Error(result.Error, http);
    }
}

internal static class ApiResults
{
    public static Results<Ok<T>, JsonHttpResult<ErrorResponse>> OkOrError<T>(Result<T> result, HttpContext http) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : Error(result.Error, http);

    public static JsonHttpResult<ErrorResponse> Error(AppError error, HttpContext http) =>
        TypedResults.Json(
            new ErrorResponse(error.Code, error.Message, Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier),
            AdminJsonContext.Default.ErrorResponse,
            statusCode: StatusFor(error.Kind));

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorKind.Internal => StatusCodes.Status500InternalServerError,
        _ => throw new UnreachableException(),
    };
}
