using TimeStore.Protocol;
using TimeStore.Protocol.Http;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;

namespace TimeStore.Server.S3;

/// <summary>
/// Runs the routed <see cref="S3Operation"/>. The switch names every operation (closed hierarchy): adding one to
/// Protocol breaks the build here until it is handled. Operations leave the NotImplemented arm as they land
/// (objects in phase 2).
/// </summary>
internal sealed class S3Dispatcher(S3BucketHandlers buckets, S3ObjectHandlers objects, S3UploadHandlers uploads)
{
    public async Task DispatchAsync(HttpContext context)
    {
        try
        {
            await RunAsync(context);
        }
        catch (S3BodyVerificationException failure) when (!context.Response.HasStarted)
        {
            // A body that broke its integrity promise (signature, hash, checksum, framing): nothing was committed.
            await S3Errors.WriteAsync(context, failure.Error);
        }
    }

    private Task RunAsync(HttpContext context)
    {
        var feature = context.Features.Get<S3RequestFeature>()
            ?? throw new InvalidOperationException("The S3 routing middleware did not run.");
        var principal = feature.Principal
            ?? throw new InvalidOperationException("The S3 authentication middleware did not run.");

        // Refuse what cannot be honoured (encryption, ACLs, locks, tags, ...) before anything is read or written.
        var headers = context.Request.Headers;
        if (S3UnsupportedHeaders.Find(feature.Operation, headers.Keys, name => headers[name].ToString()) is { } unsupported)
            return S3Errors.WriteAsync(context, unsupported);

        return feature.Operation switch
        {
            ListBuckets => buckets.ListBucketsAsync(context, principal),
            CreateBucket operation => buckets.CreateBucketAsync(context, operation),
            DeleteBucket operation => buckets.DeleteBucketAsync(context, operation),
            HeadBucket operation => buckets.HeadBucketAsync(context, operation),
            GetBucketLocation operation => buckets.GetBucketLocationAsync(context, operation),
            GetBucketVersioning operation => buckets.GetBucketVersioningAsync(context, operation),
            ListObjects operation => buckets.ListObjectsAsync(context, operation, principal),
            ListObjectsV2 operation => buckets.ListObjectsV2Async(context, operation, principal),
            ListObjectVersions operation => buckets.ListObjectVersionsAsync(context, operation, principal),
            PutObject operation => objects.PutObjectAsync(context, operation, feature.Body!),
            GetObject operation => objects.GetObjectAsync(context, operation),
            HeadObject operation => objects.HeadObjectAsync(context, operation),
            DeleteObject operation => objects.DeleteObjectAsync(context, operation),
            DeleteObjects operation => objects.DeleteObjectsAsync(context, operation, feature.Body!),
            CopyObject operation => objects.CopyObjectAsync(context, operation),
            CreateMultipartUpload operation => uploads.CreateMultipartUploadAsync(context, operation),
            UploadPart operation => uploads.UploadPartAsync(context, operation, feature.Body!),
            AbortMultipartUpload operation => uploads.AbortMultipartUploadAsync(context, operation),
            ListParts operation => uploads.ListPartsAsync(context, operation, principal),
            ListMultipartUploads operation => uploads.ListMultipartUploadsAsync(context, operation, principal),
            CompleteMultipartUpload operation => uploads.CompleteMultipartUploadAsync(context, operation, feature.RawTarget),

            PutBucketVersioning operation => buckets.PutBucketVersioningAsync(context, operation),
            GetBucketLifecycle operation => buckets.GetBucketLifecycleAsync(context, operation),
            PutBucketLifecycle operation => buckets.PutBucketLifecycleAsync(context, operation, feature.Body!),
            DeleteBucketLifecycle operation => buckets.DeleteBucketLifecycleAsync(context, operation),
            UploadPartCopy operation => uploads.UploadPartCopyAsync(context, operation),
            GetBucketPolicy operation => buckets.GetBucketPolicyAsync(context, operation),
            PutBucketPolicy operation => buckets.PutBucketPolicyAsync(context, operation),
            DeleteBucketPolicy operation => buckets.DeleteBucketPolicyAsync(context, operation),
            GetBucketPolicyStatus operation => buckets.GetBucketPolicyStatusAsync(context, operation),
            UnsupportedOperation => NotImplemented(context),
        };
    }

    private static Task NotImplemented(HttpContext context) =>
        S3Errors.WriteAsync(
            context, S3ErrorCodes.NotImplemented, S3Messages.NotImplemented, StatusCodes.Status501NotImplemented);
}
