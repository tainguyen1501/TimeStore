using Microsoft.AspNetCore.Http.HttpResults;
using TimeStore.Application.Features.Replication;
using TimeStore.Contracts.Common;
using TimeStore.Contracts.Replication;
using TimeStore.Server.Workers;

namespace TimeStore.Server.Admin;

/// <summary>
/// <c>/api/v1/buckets/{name}/replication</c> (docs/api/admin.md "Replication"): a bucket's replication rule. Viewers
/// read, admins change (<see cref="AdminAuthentication"/>). The destination secret is accepted, never returned.
/// </summary>
internal static class AdminReplicationApi
{
    private const string Replication = "/{name}/replication";
    private const string Test = Replication + "/test";
    private const string Retry = Replication + "/retry";
    private const string Resync = Replication + "/resync";
    private const string Saved = "saved";
    private const string Deleted = "deleted";
    private const string Retried = "retried";
    private const string Resynced = "resynced";

    public static void MapAdminReplicationApi(this WebApplication app)
    {
        var buckets = app.MapGroup(AdminRoutes.Prefix + AdminRoutes.Buckets);
        buckets.MapGet(Replication, GetAsync).Errors(401, 404);
        buckets.MapPut(Replication, SetAsync).Errors(400, 401, 403, 404, 409);
        buckets.MapDelete(Replication, DeleteAsync).Errors(401, 403, 404);
        buckets.MapPost(Test, TestAsync).Errors(401, 403, 404, 409);
        buckets.MapPost(Retry, RetryAsync).Errors(401, 403, 404);
        buckets.MapPost(Resync, ResyncAsync).Errors(401, 403, 404);
    }

    private static async Task<Results<Ok<ReplicationResponse>, JsonHttpResult<ErrorResponse>>> GetAsync(
        string name, IReplicationService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.GetAsync(name, ct), http);

    /// <summary>Tests the destination (writes and deletes a marker object), then saves the rule.</summary>
    private static async Task<Results<Ok<ReplicationResponse>, JsonHttpResult<ErrorResponse>>> SetAsync(
        string name, SetReplicationRequest request, IReplicationService service, ILogger<ReplicationWorker> logger,
        HttpContext http, CancellationToken ct)
    {
        var result = await service.SetAsync(name, request, ct);
        if (result.IsSuccess)
            ReplicationLog.RuleChanged(logger, name, Saved, http.Principal().AccessKeyId);
        return ApiResults.OkOrError(result, http);
    }

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeleteAsync(
        string name, IReplicationService service, ILogger<ReplicationWorker> logger, HttpContext http, CancellationToken ct)
    {
        var result = await service.DeleteAsync(name, ct);
        if (result.IsFailure)
            return ApiResults.Error(result.Error, http);
        ReplicationLog.RuleChanged(logger, name, Deleted, http.Principal().AccessKeyId);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ReplicationResponse>, JsonHttpResult<ErrorResponse>>> TestAsync(
        string name, IReplicationService service, HttpContext http, CancellationToken ct) =>
        ApiResults.OkOrError(await service.TestAsync(name, ct), http);

    /// <summary>Every current object under the prefix is sent again (a destination that lost data or was replaced).</summary>
    private static async Task<Results<Ok<ReplicationResponse>, JsonHttpResult<ErrorResponse>>> ResyncAsync(
        string name, IReplicationService service, ILogger<ReplicationWorker> logger, HttpContext http, CancellationToken ct)
    {
        var result = await service.ResyncAsync(name, ct);
        if (result.IsSuccess)
            ReplicationLog.RuleChanged(logger, name, Resynced, http.Principal().AccessKeyId);
        return ApiResults.OkOrError(result, http);
    }

    /// <summary>Failed changes are sent again; a rule paused by temporary failures resumes now.</summary>
    private static async Task<Results<Ok<ReplicationResponse>, JsonHttpResult<ErrorResponse>>> RetryAsync(
        string name, IReplicationService service, ILogger<ReplicationWorker> logger, HttpContext http, CancellationToken ct)
    {
        var result = await service.RetryAsync(name, ct);
        if (result.IsSuccess)
            ReplicationLog.RuleChanged(logger, name, Retried, http.Principal().AccessKeyId);
        return ApiResults.OkOrError(result, http);
    }
}
