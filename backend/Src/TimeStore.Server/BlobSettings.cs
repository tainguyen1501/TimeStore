using TimeStore.Application.Abstractions;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Blobs</c>.</summary>
public sealed class BlobSettings
{
    public const string Section = ServerOptions.Section + ":Blobs";
    private const int MaxDeleteDelaySeconds = 3600;

    /// <summary>
    /// How long a blob released by an overwrite, a delete or an abort stays readable, so GETs still reading the old
    /// version complete (a multipart object is read part by part). 0 deletes at once.
    /// </summary>
    public int DeleteDelaySeconds { get; set; } = (int)BlobDeletionOptions.DefaultDelay.TotalSeconds;

    public TimeSpan DeleteDelay => TimeSpan.FromSeconds(DeleteDelaySeconds);

    public string? Validate() =>
        DeleteDelaySeconds is < 0 or > MaxDeleteDelaySeconds
            ? $"{Section}:{nameof(DeleteDelaySeconds)} must be 0-{MaxDeleteDelaySeconds}."
            : null;
}
