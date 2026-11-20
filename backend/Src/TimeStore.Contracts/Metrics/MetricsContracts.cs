namespace TimeStore.Contracts.Metrics;

/// <summary>What the node holds and does: storage now, S3 requests since start, and the last 15 minutes of samples.</summary>
/// <param name="Replication">Every replication rule with its backlog (pending, lag, failed), by bucket name.</param>
public sealed record MetricsResponse(
    DateTime StartedAt,
    long UptimeSeconds,
    StorageMetricsResponse Storage,
    RequestMetricsResponse Requests,
    int SampleIntervalSeconds,
    IReadOnlyList<MetricsSampleResponse> Samples,
    IReadOnlyList<TimeStore.Contracts.Replication.ReplicationResponse> Replication);

/// <param name="Objects">Current objects; <paramref name="TotalBytes"/> their bytes.</param>
/// <param name="NoncurrentVersions">Older versions and delete markers kept by versioning; <paramref name="NoncurrentBytes"/> their bytes.</param>
/// <param name="DiskTotalBytes">Size of the volume holding the data directory (null when unknown).</param>
/// <param name="RetainedBlobs">
/// Blobs released by overwrites and deletes still on disk, waiting for <c>Blobs:DeleteDelaySeconds</c> or
/// <c>Blobs:RetainDeletedDays</c>; <paramref name="RetainedBytes"/> their bytes (the disk cost of the retention).
/// </param>
public sealed record StorageMetricsResponse(
    long Buckets,
    long Objects,
    long TotalBytes,
    long NoncurrentVersions,
    long NoncurrentBytes,
    long IncompleteUploads,
    long IncompleteUploadBytes,
    long? DiskTotalBytes,
    long? DiskFreeBytes,
    IReadOnlyList<BucketMetricsResponse> ByBucket,
    long RetainedBlobs = 0,
    long RetainedBytes = 0);

public sealed record BucketMetricsResponse(string Name, long Objects, long TotalBytes, long NoncurrentVersions, long NoncurrentBytes);

/// <summary>S3 requests since the server started (classes as Amazon S3 request metrics).</summary>
public sealed record RequestMetricsResponse(
    long Total,
    long Reads,
    long Writes,
    long Lists,
    long Deletes,
    long Other,
    long ClientErrors,
    long ServerErrors,
    long BytesIn,
    long BytesOut,
    double? P50Ms,
    double? P99Ms);

/// <summary>
/// One sampling interval: counts and bytes within it, latency percentiles of its requests (null without requests),
/// storage totals at its end.
/// </summary>
public sealed record MetricsSampleResponse(
    DateTime At,
    long Requests,
    long ClientErrors,
    long ServerErrors,
    long BytesIn,
    long BytesOut,
    double? P50Ms,
    double? P99Ms,
    long Objects,
    long TotalBytes);
