using Microsoft.AspNetCore.Http.HttpResults;
using TimeStore.Application.Features.Metrics;
using TimeStore.Contracts.Metrics;
using TimeStore.Server.Metrics;

namespace TimeStore.Server.Admin;

/// <summary><c>GET /api/v1/metrics</c>: storage now, S3 requests since start, the last 15 minutes of samples.</summary>
internal static class AdminMetricsApi
{
    private const string Metrics = "/metrics";
    private const double Median = 0.5;
    private const double Tail = 0.99;

    public static void MapAdminMetricsApi(this WebApplication app) =>
        app.MapGet(AdminRoutes.Prefix + Metrics, GetAsync).Errors(401);

    private static async Task<Ok<MetricsResponse>> GetAsync(
        IStorageMetricsService storage, S3RequestMetrics requests, MetricsSampler sampler, TimeProvider clock,
        CancellationToken ct)
    {
        var totals = requests.Take();
        var now = clock.GetUtcNow().UtcDateTime;
        return TypedResults.Ok(new MetricsResponse(
            sampler.StartedAt,
            (long)(now - sampler.StartedAt).TotalSeconds,
            await storage.GetAsync(ct),
            new RequestMetricsResponse(
                totals.Total,
                totals.Of(S3RequestClass.Read),
                totals.Of(S3RequestClass.Write),
                totals.Of(S3RequestClass.List),
                totals.Of(S3RequestClass.Delete),
                totals.Of(S3RequestClass.Other),
                totals.ClientErrors,
                totals.ServerErrors,
                totals.BytesIn,
                totals.BytesOut,
                totals.Percentile(Median),
                totals.Percentile(Tail)),
            (int)MetricsSampler.Interval.TotalSeconds,
            sampler.Samples()));
    }
}
