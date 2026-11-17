using TimeStore.Application.Features.Metrics;
using TimeStore.Contracts.Metrics;

namespace TimeStore.Server.Metrics;

/// <summary>
/// Every <see cref="Interval"/> stores one point (requests, errors, bytes and latency of the interval, storage totals
/// at its end) in a ring buffer of <see cref="Capacity"/> points: the last 15 minutes, in memory only.
/// </summary>
internal sealed partial class MetricsSampler(
    S3RequestMetrics requests, IStorageMetricsService storage, TimeProvider clock, ILogger<MetricsSampler> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public const int Capacity = 90;
    private const double Median = 0.5;
    private const double Tail = 0.99;

    private readonly Queue<MetricsSampleResponse> _samples = new(Capacity);
    private readonly Lock _gate = new();
    private S3RequestMetrics.Snapshot _previous = S3RequestMetrics.Snapshot.Zero;

    public DateTime StartedAt { get; } = clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<MetricsSampleResponse> Samples()
    {
        lock (_gate)
            return _samples.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogSampleFailed(logger, e); // a missed point; the next tick tries again
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        var now = requests.Take();
        var interval = now.Minus(_previous);
        _previous = now;
        var stored = await storage.GetAsync(ct);

        var sample = new MetricsSampleResponse(
            clock.GetUtcNow().UtcDateTime,
            interval.Total,
            interval.ClientErrors,
            interval.ServerErrors,
            interval.BytesIn,
            interval.BytesOut,
            interval.Percentile(Median),
            interval.Percentile(Tail),
            stored.Objects,
            stored.TotalBytes);

        lock (_gate)
        {
            if (_samples.Count == Capacity)
                _samples.Dequeue();
            _samples.Enqueue(sample);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metrics sample failed")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
