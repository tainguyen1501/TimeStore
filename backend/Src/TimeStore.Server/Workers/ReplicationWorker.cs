using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Replication;

namespace TimeStore.Server.Workers;

/// <summary>
/// Sends replicated changes in the background (<see cref="IReplicationRunner"/>). Nothing is kept in memory: after a
/// crash or a redeploy the queue in the database is where it was, and the first pass sends what waited.
/// </summary>
internal sealed class ReplicationWorker(IReplicationRunner runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RunAsync(stoppingToken);
}

/// <summary>Logs what replication does (keys and S3 error codes; never a secret, a signature or content).</summary>
internal sealed partial class ReplicationLog(ILogger<ReplicationWorker> logger) : IReplicationObserver
{
    public void Failed(Guid ruleId, string key, ReplicationOutcome outcome)
    {
        if (outcome.Failure == ReplicationFailure.Temporary)
            Paused(logger, ruleId, key, outcome.Error);
        else
            NotSent(logger, ruleId, key, outcome.Failure, outcome.Error);
    }

    public void Resumed(Guid ruleId, int failures) => ResumedAfter(logger, ruleId, failures);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Replication rule {RuleId} paused: the destination failed on {Key} ({Error}); it is tried again with a backoff")]
    private static partial void Paused(ILogger logger, Guid ruleId, string key, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replication rule {RuleId} did not send {Key}: {Failure} ({Error})")]
    private static partial void NotSent(ILogger logger, Guid ruleId, string key, ReplicationFailure failure, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replication rule {RuleId} reaches its destination again after {Failures} failures")]
    private static partial void ResumedAfter(ILogger logger, Guid ruleId, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replication of bucket {Bucket} {Action} by {AccessKeyId}")]
    public static partial void RuleChanged(ILogger logger, string bucket, string action, string accessKeyId);
}
