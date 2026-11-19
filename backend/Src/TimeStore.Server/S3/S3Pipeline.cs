using Microsoft.AspNetCore.Http.Features;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Streaming;
using TimeStore.Protocol.Routing;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Domain.Objects;
using TimeStore.Server.Admin;

namespace TimeStore.Server.S3;

/// <summary>Per-request S3 state set by the middlewares and read by later ones and the dispatcher.</summary>
internal sealed class S3RequestFeature(S3Operation operation, string rawTarget)
{
    public S3Operation Operation { get; } = operation;

    /// <summary>The request target exactly as sent (SigV4 signs these bytes).</summary>
    public string RawTarget { get; } = rawTarget;

    /// <summary>Set by authentication; null before it runs.</summary>
    public SigV4Principal? Principal { get; set; }

    /// <summary>The verifying request body (payload length, verified checksum); set by body verification.</summary>
    public S3Body? Body { get; set; }

    /// <summary>Who is calling (set by authorization): an access key, the root key, or nobody.</summary>
    public Application.Features.Policies.S3Caller? Caller { get; set; }
}

/// <summary>
/// The S3 port: a branch of thin middlewares in front of <see cref="S3Dispatcher"/>. Each middleware calls Protocol
/// (pure, unit-tested) and stops the request with an S3 error on failure. Health probes pass through to routing.
/// <code>
/// metrics → request id → routing → authentication (SigV4) → authorization (bucket policies) → body verification → dispatcher
/// </code>
/// </summary>
internal static class S3Pipeline
{
    public static WebApplication UseS3Api(this WebApplication app, ServerOptions options)
    {
        var router = app.Services.GetRequiredService<S3Router>();
        var verifier = app.Services.GetRequiredService<SigV4Verifier>();
        var checksums = app.Services.GetRequiredService<IChecksumFactory>();
        var dispatcher = app.Services.GetRequiredService<S3Dispatcher>();
        var authorization = app.Services.GetRequiredService<S3Authorization>();
        var metrics = app.Services.GetRequiredService<Metrics.S3RequestMetrics>();
        var hostId = options.NodeId;

        app.MapWhen(
            context => context.Connection.LocalPort == options.S3Port
                && !context.Request.Path.StartsWithSegments(HealthRoutes.Prefix),
            s3 =>
            {
                s3.Use((context, next) => Metrics.S3RequestMetricsMiddleware.RecordAsync(context, next, metrics));
                s3.Use((context, next) => AddRequestIds(context, next, hostId));
                s3.Use((context, next) => RouteAsync(context, next, router));
                s3.Use((context, next) => AuthenticateAsync(context, next, verifier));
                s3.Use((context, next) => AuthorizeAsync(context, next, authorization));
                s3.Use((context, next) => VerifyBody(context, next, checksums));
                s3.Run(dispatcher.DispatchAsync);
            });
        return app;
    }

    /// <summary>Every S3 response carries <c>x-amz-request-id</c> (the trace identifier) and <c>x-amz-id-2</c>.</summary>
    private static Task AddRequestIds(HttpContext context, RequestDelegate next, string hostId)
    {
        context.Response.Headers[S3HeaderNames.RequestId] = context.TraceIdentifier;
        context.Response.Headers[S3HeaderNames.HostId] = hostId;
        return next(context);
    }

    /// <summary>Parses the request line into an <see cref="S3Operation"/> (raw target: SigV4 needs the exact bytes).</summary>
    private static Task RouteAsync(HttpContext context, RequestDelegate next, S3Router router)
    {
        var request = context.Request;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget
            ?? request.PathBase + request.Path + request.QueryString;
        var copySource = request.Headers[S3HeaderNames.CopySource].ToString();

        var routed = router.Route(request.Method, request.Host.Host, rawTarget, copySource);
        if (routed.IsFailure)
            return S3Errors.WriteAsync(context, routed.Error);

        context.Features.Set(new S3RequestFeature(routed.Value, rawTarget));
        return next(context);
    }

    /// <summary>
    /// SigV4 (header or presigned URL). A request with no signature at all goes on as anonymous: only a bucket policy
    /// can let it through (step 6d); its body is an unsigned payload.
    /// </summary>
    private static async Task AuthenticateAsync(HttpContext context, RequestDelegate next, SigV4Verifier verifier)
    {
        var feature = context.Features.Get<S3RequestFeature>()!;
        var verified = await verifier.VerifyAsync(
            new HttpSigV4Request(context.Request, feature.RawTarget), context.RequestAborted);
        if (verified.IsFailure)
        {
            if (!ReferenceEquals(verified.Error, S3AuthErrors.Anonymous))
            {
                await S3Errors.WriteAsync(context, verified.Error);
                return;
            }

            feature.Principal = AnonymousPrincipal;
            await next(context);
            return;
        }

        feature.Principal = verified.Value;
        await next(context);
    }

    /// <summary>An unsigned request: no key, no signature, plain (unsigned) payload.</summary>
    private static readonly SigV4Principal AnonymousPrincipal = new(
        string.Empty, DateTime.UnixEpoch, string.Empty, string.Empty, string.Empty, [], PayloadMode.Unsigned, null, false);

    /// <summary>
    /// Bucket policies and key scopes (docs/research/s3-bucket-policy.md): refuses with AccessDenied before any body
    /// is read. DeleteObjects and copy sources are checked by their handlers (per key, and on the source).
    /// </summary>
    private static async Task AuthorizeAsync(HttpContext context, RequestDelegate next, S3Authorization authorization)
    {
        var feature = context.Features.Get<S3RequestFeature>()!;
        var principal = ReferenceEquals(feature.Principal, AnonymousPrincipal) ? null : feature.Principal;
        var caller = await authorization.CallerAsync(principal, context.RequestAborted);
        feature.Caller = caller;
        if (S3Authorization.For(feature.Operation) is { } access
            && !await authorization.IsAllowedAsync(access, caller, context, feature.Operation, context.RequestAborted))
        {
            await S3Errors.WriteAsync(context, Application.Features.Policies.PolicyErrors.AccessDenied);
            return;
        }

        // No policy can grant an operation TimeStore does not have: an anonymous caller (a browser form POST, ...) is
        // denied, as before step 6d, instead of learning that it is not implemented.
        if (caller.IsAnonymous && feature.Operation is UnsupportedOperation)
        {
            await S3Errors.WriteAsync(context, S3AuthErrors.Anonymous);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// PutObject and UploadPart take up to 5 GiB, not Kestrel's 30 MB. A plain body is capped at that size. An
    /// aws-chunked body is larger on the wire than its payload (chunk headers and signatures, any chunk size), so it is
    /// not capped by Kestrel: <c>x-amz-decoded-content-length</c> is checked against the limit before the body is read
    /// (<see cref="ObjectValidation"/>, <see cref="UploadValidation"/>), and the decoding stream refuses every byte
    /// beyond it.
    /// </summary>
    private static void AllowUploadBody(HttpContext context, S3RequestFeature feature)
    {
        long? payloadLimit = feature.Operation switch
        {
            PutObject => StoredObject.SinglePutMaxBytes,
            UploadPart => Domain.Uploads.MultipartUpload.MaxPartSize,
            _ => null,
        };
        if (payloadLimit is null)
            return;
        RequestBodyLimit.Allow(context, feature.Principal!.PayloadMode is PayloadMode.Sha256 or PayloadMode.Unsigned ? payloadLimit : null);
    }

    /// <summary>
    /// Replaces the request body with a verifying stream (aws-chunked decoding, chunk and trailer signatures,
    /// payload hash, checksums). Failures surface when the handler reads the body, before anything is committed.
    /// </summary>
    private static Task VerifyBody(HttpContext context, RequestDelegate next, IChecksumFactory checksums)
    {
        var feature = context.Features.Get<S3RequestFeature>()!;
        var request = context.Request;
        var opened = S3RequestBody.Open(
            request.Body, request.ContentLength, feature.Principal!, new HttpSigV4Request(request, feature.RawTarget),
            checksums, headerChecksumsDescribeBody: feature.Operation is not CompleteMultipartUpload);
        if (opened.IsFailure)
            return S3Errors.WriteAsync(context, opened.Error);

        AllowUploadBody(context, feature);
        if (!ReferenceEquals(opened.Value.Body, request.Body))
            context.Response.RegisterForDispose(opened.Value.Body); // returns pooled buffers at the end of the request
        request.Body = opened.Value.Body;
        feature.Body = opened.Value;
        return next(context);
    }
}
