namespace TimeStore.Application.Features.Uploads;

/// <summary>
/// Aborts uploads nobody completed or aborted (a built-in <c>AbortIncompleteMultipartUpload</c> rule). The age comes
/// from <see cref="UploadExpiryOptions"/> only, so a lifecycle rule (phase 6) can replace it without touching callers.
/// </summary>
public interface IUploadExpiryService
{
    /// <summary>Aborts every upload older than the configured age; returns how many. Does nothing when disabled.</summary>
    ValueTask<int> AbortAbandonedAsync(CancellationToken ct);
}

/// <param name="AbandonAfter">Age at which an upload is aborted; null never aborts.</param>
public sealed record UploadExpiryOptions(TimeSpan? AbandonAfter)
{
    public const int DefaultAbandonAfterDays = 7;
}
