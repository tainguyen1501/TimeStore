using System.Diagnostics;
using TimeStore.Protocol.Routing;

namespace TimeStore.Server.Metrics;

/// <summary>Request classes of the S3 port, as Amazon S3 request metrics group them.</summary>
internal enum S3RequestClass
{
    Read,
    Write,
    List,
    Delete,
    Other,
}

/// <summary>
/// S3 request counters since start: one <see cref="Record"/> per request (a few interlocked adds, no allocation, no
/// lock), read as a <see cref="Snapshot"/> by the sampler and the metrics endpoint. Latency goes into a fixed histogram;
/// percentiles are the upper bound of the bucket holding them.
/// </summary>
internal sealed class S3RequestMetrics
{
    /// <summary>Upper bounds (ms) of the latency buckets; one more bucket holds everything slower.</summary>
    public static readonly double[] LatencyBoundsMs = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    private const int ClientErrorStatus = 400;
    private const int ServerErrorStatus = 500;
    private readonly long[] _byClass = new long[Enum.GetValues<S3RequestClass>().Length];
    private readonly long[] _latency = new long[LatencyBoundsMs.Length + 1];
    private long _clientErrors;
    private long _serverErrors;
    private long _bytesIn;
    private long _bytesOut;

    public void Record(S3RequestClass requestClass, int status, long bytesIn, long bytesOut, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _byClass[(int)requestClass]);
        if (status >= ServerErrorStatus)
            Interlocked.Increment(ref _serverErrors);
        else if (status >= ClientErrorStatus)
            Interlocked.Increment(ref _clientErrors);
        if (bytesIn > 0)
            Interlocked.Add(ref _bytesIn, bytesIn);
        if (bytesOut > 0)
            Interlocked.Add(ref _bytesOut, bytesOut);
        Interlocked.Increment(ref _latency[BucketOf(elapsed.TotalMilliseconds)]);
    }

    public Snapshot Take()
    {
        var byClass = new long[_byClass.Length];
        for (var i = 0; i < byClass.Length; i++)
            byClass[i] = Volatile.Read(ref _byClass[i]);
        var latency = new long[_latency.Length];
        for (var i = 0; i < latency.Length; i++)
            latency[i] = Volatile.Read(ref _latency[i]);
        return new Snapshot(byClass, Volatile.Read(ref _clientErrors), Volatile.Read(ref _serverErrors),
            Volatile.Read(ref _bytesIn), Volatile.Read(ref _bytesOut), latency);
    }

    /// <summary>The class of an operation; the exhaustive switch breaks the build when an operation is added.</summary>
    public static S3RequestClass Classify(S3Operation? operation) => operation switch
    {
        null => S3RequestClass.Other, // the request failed before routing
        GetObject or HeadObject or HeadBucket or GetBucketLocation or GetBucketVersioning or GetBucketLifecycle
            or GetBucketPolicy or GetBucketPolicyStatus => S3RequestClass.Read,
        PutObject or CopyObject or CreateMultipartUpload or UploadPart or UploadPartCopy or CompleteMultipartUpload
            or CreateBucket or PutBucketVersioning or PutBucketLifecycle or PutBucketPolicy => S3RequestClass.Write,
        ListBuckets or ListObjects or ListObjectsV2 or ListObjectVersions or ListMultipartUploads or ListParts
            => S3RequestClass.List,
        DeleteObject or DeleteObjects or DeleteBucket or AbortMultipartUpload or DeleteBucketLifecycle
            or DeleteBucketPolicy => S3RequestClass.Delete,
        UnsupportedOperation => S3RequestClass.Other,
    };

    private static int BucketOf(double milliseconds)
    {
        for (var i = 0; i < LatencyBoundsMs.Length; i++)
        {
            if (milliseconds <= LatencyBoundsMs[i])
                return i;
        }

        return LatencyBoundsMs.Length;
    }

    /// <summary>Counters at one moment; <see cref="Minus"/> gives an interval.</summary>
    internal sealed record Snapshot(
        long[] ByClass, long ClientErrors, long ServerErrors, long BytesIn, long BytesOut, long[] Latency)
    {
        public static readonly Snapshot Zero = new(
            new long[Enum.GetValues<S3RequestClass>().Length], 0, 0, 0, 0, new long[LatencyBoundsMs.Length + 1]);

        public long Total => ByClass.Sum();

        public long Of(S3RequestClass requestClass) => ByClass[(int)requestClass];

        public Snapshot Minus(Snapshot earlier) => new(
            ByClass.Zip(earlier.ByClass, static (a, b) => a - b).ToArray(),
            ClientErrors - earlier.ClientErrors,
            ServerErrors - earlier.ServerErrors,
            BytesIn - earlier.BytesIn,
            BytesOut - earlier.BytesOut,
            Latency.Zip(earlier.Latency, static (a, b) => a - b).ToArray());

        /// <summary>The upper bound (ms) of the histogram bucket holding quantile <paramref name="q"/>; null without requests.</summary>
        public double? Percentile(double q)
        {
            var count = Latency.Sum();
            if (count == 0)
                return null;
            var rank = (long)Math.Ceiling(q * count);
            long seen = 0;
            for (var i = 0; i < Latency.Length; i++)
            {
                seen += Latency[i];
                if (seen >= rank)
                    return i < LatencyBoundsMs.Length ? LatencyBoundsMs[i] : LatencyBoundsMs[^1];
            }

            return LatencyBoundsMs[^1];
        }
    }
}

/// <summary>The first middleware of the S3 branch: times and classifies every request.</summary>
internal static class S3RequestMetricsMiddleware
{
    public static async Task RecordAsync(HttpContext context, RequestDelegate next, S3RequestMetrics metrics)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true; // answered 500 by the exception handler outside this branch
            throw;
        }
        finally
        {
            var operation = context.Features.Get<S3.S3RequestFeature>()?.Operation;
            metrics.Record(
                S3RequestMetrics.Classify(operation),
                failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode,
                context.Request.ContentLength ?? 0,
                context.Response.ContentLength ?? 0,
                Stopwatch.GetElapsedTime(started));
        }
    }
}
