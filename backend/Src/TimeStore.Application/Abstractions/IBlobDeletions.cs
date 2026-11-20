using TimeStore.Domain.Blobs;

namespace TimeStore.Application.Abstractions;

/// <summary>
/// Deletes blobs that commits released (an overwritten or deleted object, a replaced part, an aborted upload). The
/// store records each released blob in <c>pending_blob_deletions</c> in the commit's own transaction; the collector
/// behind this interface deletes it once its wait is over: the read grace (a GET still reading the old version
/// completes), or the retention that keeps older metadata backups restorable (docs/research/blob-retention.md).
/// Blobs of writes that never committed are deleted at once by the service instead (nobody can reference them).
/// </summary>
public interface IBlobDeletions
{
    /// <summary>
    /// A commit just released blobs: with no wait configured they are deleted now, otherwise on schedule. Never needed
    /// for correctness (the collector also runs on a timer); it keeps a zero wait immediate.
    /// </summary>
    ValueTask ReleasedAsync();

    /// <summary>Deletes every released blob whose wait is over now; returns how many (the timer, tests, holds ending).</summary>
    ValueTask<int> CollectAsync();
}

/// <param name="Delay">How long a released blob stays readable for reads in flight; zero deletes at once.</param>
/// <param name="Retention">How long released blobs are kept for restores of older metadata (zero: only <paramref name="Delay"/>).</param>
/// <param name="CollectInterval">How often the collector looks for blobs whose wait is over.</param>
/// <param name="NodeId">This node: it deletes only blobs on its own disk.</param>
public sealed record BlobDeletionOptions(
    TimeSpan Delay, TimeSpan Retention = default, TimeSpan CollectInterval = default, string NodeId = BlobDeletionOptions.DefaultNodeId)
{
    public const string DefaultNodeId = "node-1";
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultCollectInterval = TimeSpan.FromSeconds(10);

    /// <summary>What a released blob actually waits: the longer of the read grace and the retention.</summary>
    public TimeSpan Wait => Delay > Retention ? Delay : Retention;

    public TimeSpan Interval => CollectInterval > TimeSpan.Zero ? CollectInterval : DefaultCollectInterval;
}
