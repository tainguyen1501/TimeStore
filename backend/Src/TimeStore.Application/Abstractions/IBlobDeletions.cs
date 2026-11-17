using TimeStore.Domain.Blobs;

namespace TimeStore.Application.Abstractions;

/// <summary>
/// Deletes blobs that a commit has just released (an overwritten or deleted object, a replaced part, an aborted
/// upload) after a grace period, not at once: a GET that is still reading the old version (a multipart object is read
/// part by part) completes. Blobs of writes that never committed are deleted at once instead (nobody can read them).
/// </summary>
public interface IBlobDeletions
{
    /// <summary>Schedules the blobs for deletion after the grace period (at once when the period is zero).</summary>
    ValueTask DeferAsync(IEnumerable<BlobLocator> blobs);

    /// <summary>Whether the blob is waiting for its deletion (the orphan sweep leaves it alone).</summary>
    bool IsPending(Guid blobId);
}

/// <param name="Delay">How long a released blob stays readable; zero deletes at once.</param>
public sealed record BlobDeletionOptions(TimeSpan Delay)
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(60);
}
