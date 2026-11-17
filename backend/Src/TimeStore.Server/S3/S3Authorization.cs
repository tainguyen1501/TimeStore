using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Policies;
using TimeStore.Infra.Credentials;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;

namespace TimeStore.Server.S3;

/// <summary>The IAM action and resource of one request (docs/research/s3-bucket-policy.md).</summary>
/// <param name="Bucket">Null for requests that are not on a bucket (ListBuckets, CreateBucket): no policy applies.</param>
internal readonly record struct S3Access(string? Bucket, string Action, string Resource);

/// <summary>
/// Maps an operation to what a bucket policy is asked about, and decides. The exhaustive switch makes a new operation
/// a build error until it is mapped. DeleteObjects is checked per key by its handler, and copies check their source
/// there too.
/// </summary>
internal sealed class S3Authorization(IS3Authorizer authorizer, ICredentialStore credentials, RootCredential root)
{
    /// <summary>The access to check before the handler runs, or null when the handler checks itself.</summary>
    public static S3Access? For(S3Operation operation) => operation switch
    {
        ListBuckets => new S3Access(null, Name(S3Actions.ListAllMyBuckets), BucketPolicy.ArnPrefix),
        CreateBucket o => new S3Access(null, Name(S3Actions.CreateBucket), Arn(o.Bucket)),
        DeleteBucket o => Bucket(o.Bucket, S3Actions.DeleteBucket),
        HeadBucket o => Bucket(o.Bucket, S3Actions.ListBucket),
        GetBucketLocation o => Bucket(o.Bucket, S3Actions.GetBucketLocation),
        GetBucketVersioning o => Bucket(o.Bucket, S3Actions.GetBucketVersioning),
        PutBucketVersioning o => Bucket(o.Bucket, S3Actions.PutBucketVersioning),
        GetBucketLifecycle o => Bucket(o.Bucket, S3Actions.GetLifecycleConfiguration),
        PutBucketLifecycle o => Bucket(o.Bucket, S3Actions.PutLifecycleConfiguration),
        DeleteBucketLifecycle o => Bucket(o.Bucket, S3Actions.PutLifecycleConfiguration),
        GetBucketPolicy o => Bucket(o.Bucket, S3Actions.GetBucketPolicy),
        PutBucketPolicy o => Bucket(o.Bucket, S3Actions.PutBucketPolicy),
        DeleteBucketPolicy o => Bucket(o.Bucket, S3Actions.DeleteBucketPolicy),
        GetBucketPolicyStatus o => Bucket(o.Bucket, S3Actions.GetBucketPolicyStatus),
        ListObjects o => Bucket(o.Bucket, S3Actions.ListBucket),
        ListObjectsV2 o => Bucket(o.Bucket, S3Actions.ListBucket),
        ListObjectVersions o => Bucket(o.Bucket, S3Actions.ListBucketVersions),
        ListMultipartUploads o => Bucket(o.Bucket, S3Actions.ListBucketMultipartUploads),
        DeleteObjects => null,
        GetObject o => Object(o.Bucket, o.Key, o.VersionId is null ? S3Actions.GetObject : S3Actions.GetObjectVersion),
        HeadObject o => Object(o.Bucket, o.Key, o.VersionId is null ? S3Actions.GetObject : S3Actions.GetObjectVersion),
        PutObject o => Object(o.Bucket, o.Key, S3Actions.PutObject),
        CopyObject o => Object(o.Bucket, o.Key, S3Actions.PutObject),
        DeleteObject o => Object(o.Bucket, o.Key, o.VersionId is null ? S3Actions.DeleteObject : S3Actions.DeleteObjectVersion),
        CreateMultipartUpload o => Object(o.Bucket, o.Key, S3Actions.PutObject),
        UploadPart o => Object(o.Bucket, o.Key, S3Actions.PutObject),
        UploadPartCopy o => Object(o.Bucket, o.Key, S3Actions.PutObject),
        CompleteMultipartUpload o => Object(o.Bucket, o.Key, S3Actions.PutObject),
        AbortMultipartUpload o => Object(o.Bucket, o.Key, S3Actions.AbortMultipartUpload),
        ListParts o => Object(o.Bucket, o.Key, S3Actions.ListMultipartUploadParts),
        UnsupportedOperation => null, // answered NotImplemented whoever asks
    };

    public static S3Access Object(string bucket, string key, string action) => new(bucket, Name(action), Arn(bucket) + "/" + key);

    /// <summary>Who signed the request: the root key, a stored key with its S3 scope, or nobody (anonymous).</summary>
    public async ValueTask<S3Caller> CallerAsync(SigV4Principal? principal, CancellationToken ct)
    {
        if (principal is null || principal.AccessKeyId.Length == 0)
            return new S3Caller(null, IsRoot: false, Domain.AccessKeys.AccessKeyScopes.Policy);
        if (string.Equals(principal.AccessKeyId, root.Credential.AccessKeyId, StringComparison.Ordinal))
            return new S3Caller(principal.AccessKeyId, IsRoot: true, Domain.AccessKeys.AccessKeyScopes.Full);
        var credential = await credentials.FindAsync(principal.AccessKeyId, ct);
        return new S3Caller(principal.AccessKeyId, IsRoot: false, credential?.S3Scope ?? Domain.AccessKeys.AccessKeyScopes.Policy);
    }

    /// <summary>
    /// A check made by a handler (each key of a multi-object delete, the source of a copy) for the caller the
    /// authorization step already identified.
    /// </summary>
    public ValueTask<bool> AllowsAsync(HttpContext context, S3Access access, CancellationToken ct)
    {
        var feature = context.Features.Get<S3RequestFeature>()!;
        var caller = feature.Caller ?? throw new InvalidOperationException("The S3 authorization middleware did not run.");
        return caller.IsRoot ? ValueTask.FromResult(true) : IsAllowedAsync(access, caller, context, feature.Operation, ct);
    }

    /// <summary>Copies need read access to their source: <c>s3:GetObject</c>, or <c>s3:GetObjectVersion</c> with a version id.</summary>
    public static S3Access CopySource(Protocol.Routing.S3CopySource source) =>
        Object(source.Bucket, source.Key, source.VersionId is null ? S3Actions.GetObject : S3Actions.GetObjectVersion);

    public ValueTask<bool> IsAllowedAsync(S3Access access, S3Caller caller, HttpContext context, S3Operation operation, CancellationToken ct) =>
        caller.IsRoot
            ? ValueTask.FromResult(true) // the root key ignores policies: nothing to look up
            : authorizer.IsAllowedAsync(access.Bucket, access.Action, access.Resource, caller, () => Context(context, caller, operation), ct);

    /// <summary>The condition keys a policy may test (lower case).</summary>
    private static Dictionary<string, string> Context(HttpContext http, S3Caller caller, S3Operation operation)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PolicyContextKeys.SecureTransport] = http.Request.IsHttps ? "true" : "false",
        };
        if (http.Connection.RemoteIpAddress is { } address)
            context[PolicyContextKeys.SourceIp] = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        if (caller.AccessKeyId is { } id)
            context[PolicyContextKeys.Username] = id;

        static void Add(Dictionary<string, string> c, string key, string? value)
        {
            if (value is not null)
                c[key] = value;
        }

        switch (operation)
        {
            case ListObjects o:
                Add(context, PolicyContextKeys.Prefix, o.Prefix);
                Add(context, PolicyContextKeys.Delimiter, o.Delimiter);
                context[PolicyContextKeys.MaxKeys] = o.MaxKeys.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case ListObjectsV2 o:
                Add(context, PolicyContextKeys.Prefix, o.Prefix);
                Add(context, PolicyContextKeys.Delimiter, o.Delimiter);
                context[PolicyContextKeys.MaxKeys] = o.MaxKeys.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case ListObjectVersions o:
                Add(context, PolicyContextKeys.Prefix, o.Prefix);
                Add(context, PolicyContextKeys.Delimiter, o.Delimiter);
                break;
            case GetObject o:
                Add(context, PolicyContextKeys.VersionId, o.VersionId);
                break;
            case HeadObject o:
                Add(context, PolicyContextKeys.VersionId, o.VersionId);
                break;
            case DeleteObject o:
                Add(context, PolicyContextKeys.VersionId, o.VersionId);
                break;
        }

        return context;
    }

    private static S3Access Bucket(string bucket, string action) => new(bucket, Name(action), Arn(bucket));

    private static string Arn(string bucket) => BucketPolicy.ArnPrefix + bucket;

    private static string Name(string action) => S3Actions.Name(action);
}
