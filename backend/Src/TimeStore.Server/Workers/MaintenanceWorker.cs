using TimeStore.Application.Features.Auth;
using TimeStore.Application.Features.Maintenance;
using TimeStore.Application.Features.Uploads;

namespace TimeStore.Server.Workers;

/// <summary>
/// Background maintenance at startup and then on a fixed interval: abort abandoned multipart uploads, sweep orphan
/// blobs, purge expired admin sessions. Each task is skipped when configured off; a failure is logged and retried next time.
/// </summary>
internal sealed partial class MaintenanceWorker(
    IUploadExpiryService uploads, IBlobSweepService sweeps, IAuthService sessions, ServerOptions options,
    ILogger<MaintenanceWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.BlobSweep.Interval);
        do
        {
            if (options.Multipart.AbandonAfter is not null)
                await RunAsync(AbortAbandonedAsync, stoppingToken);
            if (options.BlobSweep.Enabled)
                await RunAsync(SweepAsync, stoppingToken);
            await RunAsync(async ct => await sessions.PurgeExpiredAsync(ct), stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task AbortAbandonedAsync(CancellationToken ct)
    {
        var aborted = await uploads.AbortAbandonedAsync(ct);
        if (aborted > 0)
            LogAborted(aborted, options.Multipart.AbandonAfterDays);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var result = await sweeps.SweepAsync(ct);
        if (result.IsSuccess)
            LogSwept(result.Value.Scanned, result.Value.Deleted, result.Value.DeletedBytes);
        else
            LogSkipped(result.Error.Code);
    }

    private async Task RunAsync(Func<CancellationToken, Task> task, CancellationToken ct)
    {
        try
        {
            await task(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception exception)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Aborted {Aborted} multipart uploads older than {Days} days")]
    private partial void LogAborted(int aborted, int days);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orphan-blob sweep: {Scanned} candidates, {Deleted} deleted ({DeletedBytes} bytes)")]
    private partial void LogSwept(int scanned, int deleted, long deletedBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orphan-blob sweep skipped: {Code}")]
    private partial void LogSkipped(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance task failed")]
    private partial void LogFailed(Exception exception);
}
