using TimeStore.Contracts.Metrics;

namespace TimeStore.Application.Features.Metrics;

/// <param name="DataPath">The data directory; its volume's size and free space are reported.</param>
public sealed record StorageMetricsOptions(string DataPath);

/// <summary>Storage now: per-bucket objects and bytes (kept by the store), incomplete uploads, disk space.</summary>
public interface IStorageMetricsService
{
    ValueTask<StorageMetricsResponse> GetAsync(CancellationToken ct);
}
