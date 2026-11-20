using TimeStore.Application.Features.Metrics;
using TimeStore.Contracts.Metrics;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services.Metrics;

public sealed class StorageMetricsService(IMetadataStore store, StorageMetricsOptions options) : IStorageMetricsService
{
    public async ValueTask<StorageMetricsResponse> GetAsync(CancellationToken ct)
    {
        var buckets = await store.ListBucketUsageAsync(ct);
        var uploads = await store.GetUploadUsageAsync(ct);
        var retained = await store.GetRetainedBlobsAsync(ct);
        var (diskTotal, diskFree) = Disk(options.DataPath);

        return new StorageMetricsResponse(
            buckets.Count,
            buckets.Sum(static b => b.ObjectCount),
            buckets.Sum(static b => b.TotalBytes),
            buckets.Sum(static b => b.VersionCount),
            buckets.Sum(static b => b.VersionBytes),
            uploads.Uploads,
            uploads.PartBytes,
            diskTotal,
            diskFree,
            buckets.Select(static b => new BucketMetricsResponse(b.BucketName, b.ObjectCount, b.TotalBytes, b.VersionCount, b.VersionBytes)).ToArray(),
            retained.Count,
            retained.Bytes);
    }

    /// <summary>The volume holding the data directory; unknown (null) when the platform cannot tell.</summary>
    private static (long? Total, long? Free) Disk(string dataPath)
    {
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(dataPath));
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }
}
