using TimeStore.Protocol.Routing;
using TimeStore.Server.Metrics;

namespace TimeStore.Server.Tests.Metrics;

public sealed class S3RequestMetricsTests
{
    [Fact]
    public void Record_CountsClassesErrorsAndBytes()
    {
        var metrics = new S3RequestMetrics();
        metrics.Record(S3RequestClass.Read, 200, 0, 1000, TimeSpan.FromMilliseconds(3));
        metrics.Record(S3RequestClass.Write, 200, 500, 0, TimeSpan.FromMilliseconds(8));
        metrics.Record(S3RequestClass.Read, 404, 0, 0, TimeSpan.FromMilliseconds(1));
        metrics.Record(S3RequestClass.Other, 503, 0, 0, TimeSpan.FromSeconds(30));

        var snapshot = metrics.Take();

        Assert.Equal(4, snapshot.Total);
        Assert.Equal(2, snapshot.Of(S3RequestClass.Read));
        Assert.Equal(1, snapshot.Of(S3RequestClass.Write));
        Assert.Equal((1L, 1L), (snapshot.ClientErrors, snapshot.ServerErrors));
        Assert.Equal((500L, 1000L), (snapshot.BytesIn, snapshot.BytesOut));
    }

    [Fact]
    public void Percentiles_AreTheUpperBoundOfTheBucket_NullWithoutRequests()
    {
        var metrics = new S3RequestMetrics();
        Assert.Null(metrics.Take().Percentile(0.5));

        for (var i = 0; i < 98; i++)
            metrics.Record(S3RequestClass.Read, 200, 0, 0, TimeSpan.FromMilliseconds(4)); // bucket ≤ 5 ms
        metrics.Record(S3RequestClass.Read, 200, 0, 0, TimeSpan.FromMilliseconds(400)); // ≤ 500 ms
        metrics.Record(S3RequestClass.Read, 200, 0, 0, TimeSpan.FromMinutes(1)); // beyond the last bound

        var snapshot = metrics.Take();
        Assert.Equal(5, snapshot.Percentile(0.5));
        Assert.Equal(500, snapshot.Percentile(0.99));
        Assert.Equal(10000, snapshot.Percentile(1.0));
    }

    [Fact]
    public void Minus_GivesTheInterval()
    {
        var metrics = new S3RequestMetrics();
        metrics.Record(S3RequestClass.Write, 200, 10, 0, TimeSpan.FromMilliseconds(1));
        var first = metrics.Take();
        metrics.Record(S3RequestClass.Write, 500, 20, 0, TimeSpan.FromMilliseconds(30));

        var interval = metrics.Take().Minus(first);

        Assert.Equal(1, interval.Total);
        Assert.Equal(1, interval.ServerErrors);
        Assert.Equal(20, interval.BytesIn);
        Assert.Equal(50, interval.Percentile(0.5));
    }

    [Fact]
    public void Classify_FollowsS3RequestMetrics()
    {
        Assert.Equal(S3RequestClass.List, S3RequestMetrics.Classify(new ListBuckets()));
        Assert.Equal(S3RequestClass.Delete, S3RequestMetrics.Classify(new DeleteBucket("b")));
        Assert.Equal(S3RequestClass.Read, S3RequestMetrics.Classify(new HeadBucket("b")));
        Assert.Equal(S3RequestClass.Other, S3RequestMetrics.Classify(new UnsupportedOperation("b", null, "acl")));
        Assert.Equal(S3RequestClass.Other, S3RequestMetrics.Classify(null));
    }
}
