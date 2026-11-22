using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Contracts.Backups;
using TimeStore.Domain.Backups;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Backups;

/// <summary>
/// One flow for every backup: validate, insert the run (the store refuses a second active one), then either wake the
/// worker (stored targets) or run it inline (a stream). The row is written before any work starts.
/// </summary>
public sealed class BackupService(
    IBackupRunStore runs,
    IBackupRunner runner,
    IBackupQueue queue,
    IEnumerable<IBackupArchiveStore> stores,
    BackupOptions options,
    TimeProvider clock)
    : IBackupService
{
    public async ValueTask<Result<BackupRunResponse>> StartAsync(StartBackupRequest request, BackupCaller caller, CancellationToken ct)
    {
        var target = request.Target ?? BackupTargetTypes.Directory;
        if (target == BackupTargetTypes.Stream)
            return BackupErrors.InvalidTarget(target); // a stream is started by downloading it (GET /backups/stream)
        if (Store(target) is null)
            return target == BackupTargetTypes.Directory ? BackupErrors.TargetNotConfigured : BackupErrors.InvalidTarget(target);
        var compression = BackupValidation.Compression(request.Compression, options);
        if (compression.IsFailure)
            return compression.Error;
        var content = BackupValidation.Content(request.Content);
        if (content.IsFailure)
            return content.Error;

        var run = BackupRun.Queue(BackupTriggers.Manual, target, compression.Value, caller.AccessKeyId, clock.UtcNowMs, content.Value);
        if (!await runs.TryCreateBackupRunAsync(run, ct))
            return BackupErrors.Active;

        queue.Enqueue(run.Id);
        return run.ToResponse();
    }

    public async ValueTask<(Result<BackupRunResponse> Result, Exception? Exception)> StreamAsync(
        string? compression, BackupCaller caller, IBackupTarget target, CancellationToken ct, string? content = null)
    {
        var chosen = BackupValidation.Compression(compression, options);
        if (chosen.IsFailure)
            return (chosen.Error, null);
        var holds = BackupValidation.Content(content);
        if (holds.IsFailure)
            return (holds.Error, null);

        var run = BackupRun.Queue(
            BackupTriggers.Api, BackupTargetTypes.Stream, chosen.Value, caller.AccessKeyId, clock.UtcNowMs, holds.Value);
        if (!await runs.TryCreateBackupRunAsync(run, ct))
            return (BackupErrors.Active, null);

        var execution = await runner.RunAsync(run.Id, target, ct);
        if (execution is null)
            return (BackupErrors.Cancelled, null); // cancelled between insert and start
        return execution.Error is { } error ? (error, execution.Exception) : (execution.Run.ToResponse(), null);
    }

    public async ValueTask<IReadOnlyList<BackupRunResponse>> ListAsync(CancellationToken ct) =>
        (await runs.ListBackupRunsAsync(BackupOptions.ListLimit, ct)).Select(run => run.ToResponse()).ToArray();

    public async ValueTask<Result<BackupRunResponse>> GetAsync(Guid runId, CancellationToken ct) =>
        await runs.FindBackupRunAsync(runId, ct) is { } run ? run.ToResponse() : BackupErrors.NotFound(runId);

    public async ValueTask<Result<BackupRunResponse>> CancelAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.FindBackupRunAsync(runId, ct);
        if (run is null)
            return BackupErrors.NotFound(runId);
        if (!run.IsActive)
            return BackupErrors.NotActive;

        // Queued: end it here (the worker then skips it). Lost to the worker meanwhile: signal the running run.
        var cancelled = new BackupOutcome(BackupStatus.Cancelled, clock.UtcNowMs, run.Progress, null, null,
            BackupErrors.Cancelled.Code, BackupErrors.Cancelled.Message);
        if (run.Status != BackupStatus.Queued || !await runs.TryFinishBackupRunAsync(runId, cancelled, ct))
            runner.Cancel(runId);

        return (await runs.FindBackupRunAsync(runId, ct))!.ToResponse();
    }

    public async ValueTask<Result<BackupArchiveDownload>> OpenArchiveAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.FindBackupRunAsync(runId, ct);
        if (run is null)
            return BackupErrors.NotFound(runId);
        if (run.Status != BackupStatus.Succeeded || run.ArchiveName is null || Store(run.TargetType) is not { } store)
            return BackupErrors.ArchiveNotFound;

        var content = await store.OpenReadAsync(run.ArchiveName, ct);
        return content is null ? BackupErrors.ArchiveNotFound : new BackupArchiveDownload(run.ArchiveName, run.Compression, content);
    }

    public async ValueTask<Result> DeleteArchiveAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.FindBackupRunAsync(runId, ct);
        if (run is null)
            return BackupErrors.NotFound(runId);
        if (run.Status != BackupStatus.Succeeded || run.ArchiveName is null || Store(run.TargetType) is not { } store)
            return BackupErrors.ArchiveNotFound;
        return await store.DeleteAsync(run.ArchiveName, ct) ? Result.Success : BackupErrors.ArchiveNotFound;
    }

    private IBackupArchiveStore? Store(string type) => stores.FirstOrDefault(store => store.Type == type);
}
