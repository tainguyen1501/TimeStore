using TimeStore.Contracts.Maintenance;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Maintenance;

/// <summary>
/// Removes orphan blobs: blob files no metadata row references (a crash between blob write and commit, or a failed
/// best-effort delete). Run by a background worker and on demand from the admin API.
/// </summary>
public interface IBlobSweepService
{
    /// <summary>One sweep; <c>MAINTENANCE_SWEEP_RUNNING</c> when another is in progress.</summary>
    ValueTask<Result<BlobSweepResponse>> SweepAsync(CancellationToken ct);
}

/// <param name="GracePeriod">
/// Blobs written more recently are never candidates: their metadata commit may still be in flight.
/// </param>
public sealed record BlobSweepOptions(TimeSpan GracePeriod)
{
    public static readonly TimeSpan MinGracePeriod = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromHours(1);
}
