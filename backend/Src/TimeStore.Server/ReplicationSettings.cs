using TimeStore.Application.Features.Replication;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Replication</c> (docs/configuration.md "Replication"). Every value has a default.</summary>
public sealed class ReplicationSettings
{
    public const string Section = ServerOptions.Section + ":Replication";
    private const int MaxConcurrency = 64;
    private const int MaxSeconds = 24 * 60 * 60;
    private const int MaxBatchDelayMilliseconds = 10_000;

    /// <summary>
    /// Run the replication worker and accept new rules. Off: nothing is sent, but buckets with a rule still queue their
    /// changes, so turning it on again catches up.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Objects sent at the same time, over every rule (1-64).</summary>
    public int Concurrency { get; set; } = ReplicationOptions.DefaultConcurrency;

    /// <summary>Seconds between reads of the queue when no commit woke the worker (a safety net, 1-86400).</summary>
    public int PollSeconds { get; set; } = (int)ReplicationOptions.DefaultPollInterval.TotalSeconds;

    /// <summary>Milliseconds the worker waits after a wake-up so a burst of changes to one key is sent once (0-10000).</summary>
    public int BatchDelayMilliseconds { get; set; } = (int)ReplicationOptions.DefaultBatchDelay.TotalMilliseconds;

    /// <summary>Longest pause, in seconds, after repeated temporary destination failures (1-86400).</summary>
    public int MaxBackoffSeconds { get; set; } = (int)ReplicationOptions.DefaultMaxBackoff.TotalSeconds;

    /// <summary>Longest time, in seconds, one request to a destination may take: a whole object or one part (10-86400).</summary>
    public int RequestTimeoutSeconds { get; set; } = (int)ReplicationOptions.DefaultRequestTimeout.TotalSeconds;

    public ReplicationOptions ToOptions() => new(
        Enabled, Concurrency, TimeSpan.FromSeconds(PollSeconds), TimeSpan.FromMilliseconds(BatchDelayMilliseconds),
        TimeSpan.FromSeconds(MaxBackoffSeconds), TimeSpan.FromSeconds(RequestTimeoutSeconds));

    public string? Validate()
    {
        if (Concurrency is < 1 or > MaxConcurrency)
            return $"{Section}:{nameof(Concurrency)} must be 1-{MaxConcurrency}.";
        if (PollSeconds is < 1 or > MaxSeconds)
            return $"{Section}:{nameof(PollSeconds)} must be 1-{MaxSeconds}.";
        if (BatchDelayMilliseconds is < 0 or > MaxBatchDelayMilliseconds)
            return $"{Section}:{nameof(BatchDelayMilliseconds)} must be 0-{MaxBatchDelayMilliseconds}.";
        if (MaxBackoffSeconds is < 1 or > MaxSeconds)
            return $"{Section}:{nameof(MaxBackoffSeconds)} must be 1-{MaxSeconds}.";
        if (RequestTimeoutSeconds is < 10 or > MaxSeconds)
            return $"{Section}:{nameof(RequestTimeoutSeconds)} must be 10-{MaxSeconds}.";
        return null;
    }
}
