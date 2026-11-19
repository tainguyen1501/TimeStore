using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Auth;
using TimeStore.Application.Features.Backups;
using TimeStore.Contracts.Backups;
using TimeStore.Contracts.Common;
using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Backups;
using TimeStore.Server.Workers;

namespace TimeStore.Server.Admin;

/// <summary>
/// <c>/api/v1/backups</c> (docs/api/admin.md "Backups"): an API for programs, not for the admin UI. Requests are signed
/// with SigV4 by a key with the <c>admin</c> or <c>backup</c> role (<see cref="AdminAuthentication"/>; sign-in
/// cookies are refused), reads included: an archive is the whole server. Only admin keys may include the master key.
/// </summary>
internal static class AdminBackupApi
{
    private const string Backups = "/backups";

    /// <summary>Every backup route starts here (the authentication middleware selects SigV4 by it).</summary>
    public const string RoutePrefix = AdminRoutes.Prefix + Backups;
    private const string ById = "/{id:guid}";
    private const string Cancel = "/{id:guid}/cancel";
    private const string Archive = "/{id:guid}/archive";
    private const string Stream = "/stream";
    private const string NoStore = "no-store";
    private const string ZstdContentType = "application/zstd";
    private const string TarContentType = "application/x-tar";
    private const string Attachment = "attachment";

    public static void MapAdminBackupApi(this WebApplication app)
    {
        var backups = app.MapGroup(AdminRoutes.Prefix + Backups)
            .AddEndpointFilter(async (context, next) =>
                AccessKeyRoles.CanBackUp(context.HttpContext.Principal().Role)
                    ? await next(context)
                    : ApiResults.Error(AuthErrors.Forbidden, context.HttpContext));

        backups.MapPost(AdminRoutes.Root, StartAsync).Errors(400, 401, 403, 409);
        backups.MapGet(AdminRoutes.Root, ListAsync).Errors(401, 403);
        backups.MapGet(Stream, StreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: ZstdContentType, additionalContentTypes: [TarContentType])
            .Errors(400, 401, 403, 409);
        backups.MapGet(ById, GetAsync).Errors(401, 403, 404);
        backups.MapPost(Cancel, CancelAsync).Errors(401, 403, 404, 409);
        backups.MapGet(Archive, DownloadAsync)
            .Produces(StatusCodes.Status200OK, contentType: ZstdContentType, additionalContentTypes: [TarContentType])
            .Errors(401, 403, 404);
        backups.MapDelete(Archive, DeleteArchiveAsync).Errors(401, 403, 404);
    }

    /// <summary>Frees the space of an archive once it is taken (the run stays in the history).</summary>
    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeleteArchiveAsync(
        Guid id, IBackupService service, ILogger<BackupWorker> logger, HttpContext http, CancellationToken ct)
    {
        var result = await service.DeleteArchiveAsync(id, ct);
        if (result.IsFailure)
            return ApiResults.Error(result.Error, http);
        BackupLog.ArchiveDeleted(logger, id, http.Principal().AccessKeyId);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted<BackupRunResponse>, JsonHttpResult<ErrorResponse>>> StartAsync(
        StartBackupRequest request, IBackupService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.StartAsync(request, CallerOf(http), ct);
        return result.IsSuccess
            ? TypedResults.Accepted($"{AdminRoutes.Prefix}{Backups}/{result.Value.Id}", result.Value)
            : ApiResults.Error(result.Error, http);
    }

    private static async Task<Ok<IReadOnlyList<BackupRunResponse>>> ListAsync(IBackupService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListAsync(ct));

    private static async Task<Results<Ok<BackupRunResponse>, JsonHttpResult<ErrorResponse>>> GetAsync(
        Guid id, IBackupService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.GetAsync(id, ct), http);

    private static async Task<Results<Ok<BackupRunResponse>, JsonHttpResult<ErrorResponse>>> CancelAsync(
        Guid id, IBackupService service, ILogger<BackupWorker> logger, HttpContext http, CancellationToken ct)
    {
        var result = await service.CancelAsync(id, ct);
        if (result.IsSuccess)
            BackupLog.CancelRequested(logger, id, http.Principal().AccessKeyId);
        return ApiResults.OkOrError(result, http);
    }

    /// <summary>The archive of a succeeded run kept in the backup directory.</summary>
    private static async Task<IResult> DownloadAsync(
        Guid id, IBackupService service, ILogger<BackupWorker> logger, HttpContext http, CancellationToken ct)
    {
        var result = await service.OpenArchiveAsync(id, ct);
        if (result.IsFailure)
            return ApiResults.Error(result.Error, http);

        BackupLog.Downloaded(logger, id, result.Value.Name, http.Principal().AccessKeyId);
        http.Response.Headers.CacheControl = NoStore;
        return TypedResults.Stream(result.Value.Content, ContentTypeOf(result.Value.Compression), result.Value.Name);
    }

    /// <summary>
    /// A backup written straight into the response (curl, a pre-backup hook, TimeOps): nothing is kept on the server.
    /// Refusals come back as JSON before the first byte; a failure after it aborts the connection, so a client never
    /// takes a truncated archive for a whole one (the archive also ends with <c>blobs.sha256</c>).
    /// </summary>
    private static async Task<IResult> StreamAsync(
        IBackupService service, ILogger<BackupWorker> logger, HttpContext http, [FromQuery] string? compression = null)
    {
        var principal = http.Principal();
        var target = new HttpBackupTarget(http.Response);
        var (result, exception) = await service.StreamAsync(compression, CallerOf(http), target, http.RequestAborted);
        if (result.IsSuccess)
        {
            BackupLog.Streamed(logger, result.Value.Id, result.Value.ArchiveName, principal.AccessKeyId, result.Value.ArchiveSize ?? 0);
            return TypedResults.Empty;
        }

        BackupLog.StreamEnded(logger, exception, principal.AccessKeyId, result.Error.Code);

        if (!http.Response.HasStarted)
        {
            // Nothing sent yet: answer the error as JSON, not as a file named after the archive.
            http.Response.Headers.Remove(HeaderNames.ContentDisposition);
            return ApiResults.Error(result.Error, http);
        }

        http.Abort();
        return TypedResults.Empty;
    }

    private static BackupCaller CallerOf(HttpContext http) => new(http.Principal().AccessKeyId);

    private static string ContentTypeOf(string compression) =>
        compression == BackupCompressions.Zstd ? ZstdContentType : TarContentType;

    /// <summary>The <c>stream</c> target: the archive is the response body, named by <c>Content-Disposition</c>.</summary>
    private sealed class HttpBackupTarget(HttpResponse response) : IBackupTarget
    {
        public string Type => BackupTargetTypes.Stream;

        public ValueTask<IBackupUpload> OpenWriteAsync(string archiveName, CancellationToken ct)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = archiveName.EndsWith(Infra.Backups.TarBackupArchiveFormat.ZstdExtension, StringComparison.Ordinal)
                ? ZstdContentType
                : TarContentType;
            response.Headers.CacheControl = NoStore;
            var disposition = new ContentDispositionHeaderValue(Attachment);
            disposition.SetHttpFileName(archiveName);
            response.Headers.ContentDisposition = disposition.ToString();
            return ValueTask.FromResult<IBackupUpload>(new Upload(response));
        }

        private sealed class Upload(HttpResponse response) : IBackupUpload
        {
            public System.IO.Stream Stream => response.Body;

            public async ValueTask CommitAsync(CancellationToken ct) => await response.Body.FlushAsync(ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
