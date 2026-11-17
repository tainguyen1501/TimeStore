using TimeStore.Application.Features.Uploads;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Multipart</c>.</summary>
public sealed class MultipartSettings
{
    public const string Section = ServerOptions.Section + ":Multipart";
    private const int MaxAbandonAfterDays = 3650;

    /// <summary>
    /// Uploads older than this many days are aborted by the maintenance worker (like an AbortIncompleteMultipartUpload
    /// lifecycle rule). 0 never aborts.
    /// </summary>
    public int AbandonAfterDays { get; set; } = UploadExpiryOptions.DefaultAbandonAfterDays;

    public TimeSpan? AbandonAfter => AbandonAfterDays == 0 ? null : TimeSpan.FromDays(AbandonAfterDays);

    public string? Validate() =>
        AbandonAfterDays is < 0 or > MaxAbandonAfterDays
            ? $"{Section}:{nameof(AbandonAfterDays)} must be 0 (never) to {MaxAbandonAfterDays}."
            : null;
}
