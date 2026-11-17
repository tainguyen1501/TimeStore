using TimeStore.Application.Features.Lifecycle;

namespace TimeStore.Server.Workers;

/// <summary>Applies bucket lifecycle rules every <see cref="LifecycleSettings.Interval"/> (docs/research/s3-lifecycle.md).</summary>
internal sealed partial class LifecycleWorker(ILifecycleRunner runner, ServerOptions options, ILogger<LifecycleWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Lifecycle.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var run = await runner.RunAsync(stoppingToken);
                if (run.Expired + run.NoncurrentRemoved + run.DeleteMarkersRemoved + run.UploadsAborted > 0)
                    LogRun(logger, run.Expired, run.NoncurrentRemoved, run.DeleteMarkersRemoved, run.UploadsAborted);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e); // the next tick tries again
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Lifecycle: {Expired} expired, {Noncurrent} noncurrent versions removed, {Markers} delete markers removed, {Uploads} uploads aborted")]
    private static partial void LogRun(ILogger logger, int expired, int noncurrent, int markers, int uploads);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lifecycle run failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
