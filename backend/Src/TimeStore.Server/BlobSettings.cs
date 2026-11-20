using TimeStore.Application.Abstractions;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Blobs</c>.</summary>
public sealed class BlobSettings
{
    public const string Section = ServerOptions.Section + ":Blobs";
    private const int MaxDeleteDelaySeconds = 3600;
    private const int MaxRetainDeletedDays = 3650;
    private const int MaxCollectSeconds = 3600;

    /// <summary>
    /// How long a blob released by an overwrite, a delete or an abort stays readable, so GETs still reading the old
    /// version complete (a multipart object is read part by part). 0 deletes at once.
    /// </summary>
    public int DeleteDelaySeconds { get; set; } = (int)BlobDeletionOptions.DefaultDelay.TotalSeconds;

    public TimeSpan DeleteDelay => TimeSpan.FromSeconds(DeleteDelaySeconds);

    /// <summary>
    /// Days a released blob is kept before it is deleted, so a metadata backup taken before the release can still be
    /// restored (docs/research/blob-retention.md). Set it to how long metadata backups are kept plus a margin (backups
    /// kept 30 days: 35). 0 keeps only <see cref="DeleteDelaySeconds"/>. Everything overwritten or deleted stays on disk
    /// this long (metrics: retained bytes).
    /// </summary>
    public int RetainDeletedDays { get; set; }

    /// <summary>How often, in seconds, released blobs whose wait is over are deleted (1-3600).</summary>
    public int CollectSeconds { get; set; } = (int)BlobDeletionOptions.DefaultCollectInterval.TotalSeconds;

    public BlobDeletionOptions ToOptions(string nodeId) => new(
        DeleteDelay, TimeSpan.FromDays(RetainDeletedDays), TimeSpan.FromSeconds(CollectSeconds), nodeId);

    public string? Validate()
    {
        if (DeleteDelaySeconds is < 0 or > MaxDeleteDelaySeconds)
            return $"{Section}:{nameof(DeleteDelaySeconds)} must be 0-{MaxDeleteDelaySeconds}.";
        if (RetainDeletedDays is < 0 or > MaxRetainDeletedDays)
            return $"{Section}:{nameof(RetainDeletedDays)} must be 0-{MaxRetainDeletedDays}.";
        if (CollectSeconds is < 1 or > MaxCollectSeconds)
            return $"{Section}:{nameof(CollectSeconds)} must be 1-{MaxCollectSeconds}.";
        return null;
    }
}
