using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Policies;
using TimeStore.Protocol.Auth;
using TimeStore.Contracts.Common;
using TimeStore.Contracts.Objects;
using TimeStore.Domain.Objects;

namespace TimeStore.Server.Admin;

/// <summary>
/// The object browser of the admin UI (step 7d). Keys travel in the query string (<c>?key=</c>), never in the route, so
/// any key round-trips. Same session, role and CSRF rules as the rest of <c>/api/v1</c> (<see cref="AdminAuthentication"/>).
/// </summary>
internal static class AdminObjectApi
{
    private const string Objects = "/{bucket}/objects";
    private const string Versions = "/{bucket}/objects/versions";
    private const string Restore = "/{bucket}/objects/restore";
    private const string Details = "/{bucket}/objects/details";
    private const string Content = "/{bucket}/objects/content";
    private const string Delete = "/{bucket}/objects/delete";
    private const string Uploads = "/{bucket}/uploads";
    private const string Part = "/{bucket}/uploads/{uploadId}/parts/{partNumber:int}";
    private const string Complete = "/{bucket}/uploads/{uploadId}/complete";
    private const string Upload = "/{bucket}/uploads/{uploadId}";
    private const string Access = "/{bucket}/objects/access";
    private const string Share = "/{bucket}/objects/share";
    private const string VersionIdParameter = "versionId";
    private const string FallbackFileName = "download";
    private const string NoStore = "no-store";

    /// <summary>A download never runs as a page on the admin origin (stored XSS): no scripts, no plugins, no forms.</summary>
    private const string DownloadPolicy = "sandbox";
    private const string NoSniff = "nosniff";

    public static void MapAdminObjectApi(this WebApplication app)
    {
        var buckets = app.MapGroup(AdminRoutes.Prefix + AdminRoutes.Buckets);
        buckets.MapGet(Objects, ListAsync).Errors(400, 401, 404);
        buckets.MapGet(Versions, ListVersionsAsync).Errors(400, 401, 404);
        buckets.MapPost(Restore, RestoreAsync).Errors(400, 401, 403, 404);
        buckets.MapGet(Details, DetailsAsync).Errors(400, 401, 404);
        buckets.MapGet(Content, DownloadAsync)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Errors(400, 401, 404);
        buckets.MapPut(Content, PutAsync).Errors(400, 401, 403, 404);
        buckets.MapPost(Delete, DeleteAsync).Errors(400, 401, 403, 404);
        buckets.MapPost(Uploads, StartUploadAsync).Errors(400, 401, 403, 404);
        buckets.MapPut(Part, UploadPartAsync).Errors(400, 401, 403, 404);
        buckets.MapPost(Complete, CompleteUploadAsync).Errors(400, 401, 403, 404);
        buckets.MapDelete(Upload, AbortUploadAsync).Errors(401, 403, 404);
        buckets.MapGet(Access, AccessAsync).Errors(400, 401, 404);
        buckets.MapPost(Share, ShareAsync).Errors(400, 401, 403, 404);
    }

    /// <summary>The object's S3 URL and whether anyone may read it without signing (step 7f).</summary>
    private static async Task<Results<Ok<ObjectAccessResponse>, JsonHttpResult<ErrorResponse>>> AccessAsync(
        string bucket, [FromQuery] string key, [FromQuery] string? versionId, IObjectBrowserService service,
        IS3Authorizer authorizer, ServerOptions options, HttpContext http, CancellationToken ct)
    {
        var details = await service.DetailsAsync(bucket, key, versionId, ct);
        if (details.IsFailure)
            return ApiResults.Error(details.Error, http);

        var resource = BucketPolicy.ArnPrefix + bucket + "/" + key;
        var action = S3Actions.Name(versionId is null ? S3Actions.GetObject : S3Actions.GetObjectVersion);
        var anonymous = new S3Caller(null, IsRoot: false, Domain.AccessKeys.AccessKeyScopes.Policy);
        // An empty context: a statement with conditions (source IP, TLS, ...) does not make the object public.
        var isPublic = await authorizer.IsAllowedAsync(bucket, action, resource, anonymous, static () => EmptyContext, ct);
        var url = ObjectUri(S3Endpoint(options, http), bucket, key, versionId);
        return TypedResults.Ok(new ObjectAccessResponse(url, isPublic));
    }

    private static readonly Dictionary<string, string> EmptyContext = new(StringComparer.Ordinal);

    /// <summary>
    /// A presigned GET URL signed with the signed-in key (step 7f): disabling or deleting that key revokes every link
    /// it signed. Admins only (POST). The URL is a bearer credential for this one object: it is returned once and
    /// never logged.
    /// </summary>
    private static async Task<Results<Ok<ShareObjectResponse>, JsonHttpResult<ErrorResponse>>> ShareAsync(
        string bucket, ShareObjectRequest request, IObjectBrowserService service, ISecretKeyResolver secrets,
        ServerOptions options, TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        if (request.ExpiresInSeconds is < 1 or > SigV4Presigner.MaxExpiresSeconds)
            return ApiResults.Error(ObjectErrors.InvalidShareExpiry(SigV4Presigner.MaxExpiresSeconds), http);
        var details = await service.DetailsAsync(bucket, request.Key ?? string.Empty, request.VersionId, ct);
        if (details.IsFailure)
            return ApiResults.Error(details.Error, http);

        var signer = http.Principal().AccessKeyId;
        var secret = await secrets.FindSecretKeyAsync(signer, ct)
            ?? throw new InvalidOperationException("The signed-in key has no secret any more.");
        var now = clock.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); // SigV4 has whole seconds
        var query = request.VersionId is { } version
            ? new[] { KeyValuePair.Create(VersionIdParameter, version) }
            : [];
        var url = SigV4Presigner.Presign(
            S3Endpoint(options, http), "/" + bucket + "/" + request.Key, query, signer, secret, options.S3.Region, now,
            request.ExpiresInSeconds);
        http.Response.Headers.CacheControl = NoStore;
        return TypedResults.Ok(new ShareObjectResponse(url, now.AddSeconds(request.ExpiresInSeconds), signer));
    }

    /// <summary><c>TimeStore:S3:PublicUrl</c>, else the host the admin UI was reached on, with the S3 port.</summary>
    private static Uri S3Endpoint(ServerOptions options, HttpContext http) =>
        options.S3.PublicEndpoint
        ?? new UriBuilder(http.Request.Scheme, http.Request.Host.Host, options.S3Port).Uri;

    private static string ObjectUri(Uri endpoint, string bucket, string key, string? versionId)
    {
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        var path = SigV4Canonical.Encode("/" + bucket + "/" + key, keepSlash: true);
        return $"{endpoint.Scheme}://{host}{path}"
            + (versionId is null ? string.Empty : $"?{VersionIdParameter}={SigV4Canonical.Encode(versionId, keepSlash: false)}");
    }

    private static async Task<Results<Ok<ObjectListResponse>, JsonHttpResult<ErrorResponse>>> ListAsync(
        string bucket, [FromQuery] string? prefix, [FromQuery] string? startAfter, [FromQuery] int? limit,
        IObjectBrowserService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(
            await service.ListAsync(bucket, prefix ?? string.Empty, startAfter, limit ?? ObjectBrowserLimits.DefaultPageSize, ct), http);

    private static async Task<Results<Ok<ObjectVersionListResponse>, JsonHttpResult<ErrorResponse>>> ListVersionsAsync(
        string bucket, [FromQuery] string? prefix, [FromQuery] string? keyMarker, [FromQuery] string? versionIdMarker,
        [FromQuery] int? limit, IObjectBrowserService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.ListVersionsAsync(
            bucket, prefix ?? string.Empty, keyMarker, versionIdMarker, limit ?? ObjectBrowserLimits.DefaultPageSize, ct), http);

    private static async Task<Results<Ok<ObjectWrittenResponse>, JsonHttpResult<ErrorResponse>>> RestoreAsync(
        string bucket, RestoreVersionRequest request, IObjectBrowserService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.RestoreAsync(bucket, request, ct), http);

    private static async Task<Results<Ok<ObjectDetailsResponse>, JsonHttpResult<ErrorResponse>>> DetailsAsync(
        string bucket, [FromQuery] string key, [FromQuery] string? versionId, IObjectBrowserService service, HttpContext http,
        CancellationToken ct) =>
        ApiResults.OkOrError(await service.DetailsAsync(bucket, key, versionId, ct), http);

    /// <summary>Always an attachment, sandboxed and never sniffed, whatever the stored content type.</summary>
    private static async Task<Results<FileStreamHttpResult, JsonHttpResult<ErrorResponse>>> DownloadAsync(
        string bucket, [FromQuery] string key, [FromQuery] string? versionId, IObjectBrowserService service, HttpContext http,
        CancellationToken ct)
    {
        var opened = await service.OpenAsync(bucket, key, versionId, ct);
        if (opened.IsFailure)
            return ApiResults.Error(opened.Error, http);

        var reader = opened.Value;
        var headers = http.Response.Headers;
        headers[HeaderNames.ContentSecurityPolicy] = DownloadPolicy;
        headers[HeaderNames.XContentTypeOptions] = NoSniff;
        headers[HeaderNames.CacheControl] = NoStore;
        http.Response.RegisterForDisposeAsync(reader);
        return TypedResults.Stream(
            reader.Content,
            reader.Object.ContentType,
            FileName(key),
            new DateTimeOffset(reader.Object.CreatedAt, TimeSpan.Zero),
            new EntityTagHeaderValue($"\"{reader.Object.ETag}\""));
    }

    /// <summary>The raw request body is the object (streamed into the blob store); Content-Length is required.</summary>
    private static async Task<Results<Ok<ObjectWrittenResponse>, JsonHttpResult<ErrorResponse>>> PutAsync(
        string bucket, [FromQuery] string key, IObjectBrowserService service, HttpContext http, CancellationToken ct)
    {
        RequestBodyLimit.Allow(http, StoredObject.SinglePutMaxBytes);
        return ApiResults.OkOrError(
            await service.PutAsync(bucket, key, http.Request.ContentType, http.Request.ContentLength, http.Request.Body, ct), http);
    }

    private static async Task<Results<Ok<DeleteObjectsResponse>, JsonHttpResult<ErrorResponse>>> DeleteAsync(
        string bucket, DeleteObjectsRequest request, IObjectBrowserService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.DeleteAsync(bucket, request, ct), http);

    private static async Task<Results<Ok<StartUploadResponse>, JsonHttpResult<ErrorResponse>>> StartUploadAsync(
        string bucket, StartUploadRequest request, IObjectBrowserService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.StartUploadAsync(bucket, request, ct), http);

    private static async Task<Results<Ok<UploadedPartResponse>, JsonHttpResult<ErrorResponse>>> UploadPartAsync(
        string bucket, string uploadId, int partNumber, [FromQuery] string key, IObjectBrowserService service,
        HttpContext http, CancellationToken ct)
    {
        RequestBodyLimit.Allow(http, StoredObject.SinglePutMaxBytes);
        return ApiResults.OkOrError(
            await service.UploadPartAsync(bucket, key, uploadId, partNumber, http.Request.ContentLength, http.Request.Body, ct), http);
    }

    private static async Task<Results<Ok<ObjectWrittenResponse>, JsonHttpResult<ErrorResponse>>> CompleteUploadAsync(
        string bucket, string uploadId, [FromQuery] string key, CompleteUploadRequest request, IObjectBrowserService service,
        HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.CompleteUploadAsync(bucket, key, uploadId, request, ct), http);

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> AbortUploadAsync(
        string bucket, string uploadId, [FromQuery] string key, IObjectBrowserService service, HttpContext http,
        CancellationToken ct)
    {
        var result = await service.AbortUploadAsync(bucket, key, uploadId, ct);
        return result.IsSuccess ? TypedResults.NoContent() : ApiResults.Error(result.Error, http);
    }

    /// <summary>The last segment of the key ("photos/2026/cat.jpg" → "cat.jpg").</summary>
    private static string FileName(string key)
    {
        var name = key.TrimEnd('/');
        var slash = name.LastIndexOf('/');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        return name.Length > 0 ? name : FallbackFileName;
    }
}
