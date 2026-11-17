using TimeStore.Primitives;
using TimeStore.Protocol.Routing;

namespace TimeStore.Protocol.Http;

/// <summary>Requests that ask for functionality TimeStore does not have.</summary>
public static class S3HeaderErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string NotImplemented = "S3_HEADER_NOT_IMPLEMENTED";
        public const string AclNotSupported = "S3_ACL_NOT_SUPPORTED";
        public const string BucketAclNotSupported = "S3_BUCKET_ACL_NOT_SUPPORTED";
    }

    /// <summary>Names the header, never its value (SSE-C headers carry an encryption key).</summary>
    public static AppError NotImplemented(string header) => AppError.Validation(
        Codes.NotImplemented, $"The {header} header implies functionality that is not implemented.");

    public static readonly AppError AclNotSupported = AppError.Validation(Codes.AclNotSupported, "The bucket does not allow ACLs");

    public static readonly AppError BucketAclNotSupported = AppError.Validation(
        Codes.BucketAclNotSupported, "Bucket cannot have ACLs set with ObjectOwnership's BucketOwnerEnforced setting");
}

/// <summary>
/// Refuses requests whose headers ask for a feature TimeStore does not implement, instead of silently dropping the
/// header (a client would believe its object is encrypted, locked, tagged or public when it is not). Values that mean
/// "the default" pass: <c>x-amz-storage-class: STANDARD</c>, <c>x-amz-object-ownership: BucketOwnerEnforced</c>,
/// <c>x-amz-bucket-object-lock-enabled: false</c>. ACLs follow Amazon S3 for buckets with ACLs disabled (Bucket owner
/// enforced, the default for new buckets): object writes accept no ACL or <c>bucket-owner-full-control</c> only, any
/// other (<c>private</c> included) is <c>AccessControlListNotSupported</c>; CreateBucket accepts an owner-only ACL
/// (<c>private</c>) and refuses grants to others with <c>InvalidBucketAclWithObjectOwnership</c>. Everything else is
/// <c>NotImplemented</c>.
/// </summary>
public static class S3UnsupportedHeaders
{
    private const string Acl = "x-amz-acl";
    private const string GrantPrefix = "x-amz-grant-";
    private const string StorageClass = "x-amz-storage-class";
    private const string Tagging = "x-amz-tagging";
    private const string WebsiteRedirect = "x-amz-website-redirect-location";
    private const string WriteOffset = "x-amz-write-offset-bytes";
    private const string ObjectLockPrefix = "x-amz-object-lock-";
    private const string BucketObjectLock = "x-amz-bucket-object-lock-enabled";
    private const string ObjectOwnership = "x-amz-object-ownership";

    /// <summary>SSE-S3 and SSE-KMS (<c>x-amz-server-side-encryption</c>, <c>…-aws-kms-key-id</c>, <c>…-context</c>, …).</summary>
    private const string ServerSideEncryptionPrefix = "x-amz-server-side-encryption";

    /// <summary>SSE-C (<c>…-customer-algorithm</c>, <c>…-customer-key</c>, <c>…-customer-key-MD5</c>), also on reads.</summary>
    private const string CustomerKeyPrefix = "x-amz-server-side-encryption-customer-";

    private const string CopySourceCustomerKeyPrefix = "x-amz-copy-source-server-side-encryption-customer-";

    /// <summary>The only ACL an object write may carry when ACLs are disabled (Amazon S3).</summary>
    private static readonly string[] ObjectAcls = ["bucket-owner-full-control"];

    /// <summary>Owner-only bucket ACLs, which Amazon S3 accepts on CreateBucket with ACLs disabled.</summary>
    private static readonly string[] BucketAcls = ["private"];
    private const string StandardStorageClass = "STANDARD";
    private const string OwnerEnforced = "BucketOwnerEnforced";
    private const string False = "false";

    /// <summary>The first unsupported header of <paramref name="operation"/>, or null when the request is fine.</summary>
    public static AppError? Find(S3Operation operation, IEnumerable<string> headerNames, Func<string, string?> header) =>
        operation switch
        {
            PutObject or CreateMultipartUpload or CopyObject => FindInObjectWrite(headerNames, header),
            UploadPart or UploadPartCopy or GetObject or HeadObject or CompleteMultipartUpload => FindCustomerKey(headerNames),
            CreateBucket => FindInCreateBucket(headerNames, header),
            _ => null,
        };

    /// <summary>Headers that set properties of a new object.</summary>
    private static AppError? FindInObjectWrite(IEnumerable<string> headerNames, Func<string, string?> header)
    {
        foreach (var name in headerNames)
        {
            if (Is(name, Acl))
            {
                if (!IsOneOf(header(name), ObjectAcls))
                    return S3HeaderErrors.AclNotSupported;
            }
            else if (StartsWith(name, GrantPrefix))
                return S3HeaderErrors.AclNotSupported;
            else if (Is(name, StorageClass))
            {
                if (!IsOneOf(header(name), [StandardStorageClass]))
                    return S3HeaderErrors.NotImplemented(StorageClass);
            }
            else if (StartsWith(name, ServerSideEncryptionPrefix) || StartsWith(name, CopySourceCustomerKeyPrefix)
                || StartsWith(name, ObjectLockPrefix) || Is(name, Tagging) || Is(name, WebsiteRedirect) || Is(name, WriteOffset))
                return S3HeaderErrors.NotImplemented(name.ToLowerInvariant());
        }

        return null;
    }

    /// <summary>
    /// SSE-C on operations that do not create the object: a client sending its key expects the server to use it (to
    /// encrypt a part, to decrypt on read), and nothing here would.
    /// </summary>
    private static AppError? FindCustomerKey(IEnumerable<string> headerNames)
    {
        foreach (var name in headerNames)
        {
            if (StartsWith(name, CustomerKeyPrefix) || StartsWith(name, CopySourceCustomerKeyPrefix))
                return S3HeaderErrors.NotImplemented(name.ToLowerInvariant());
        }

        return null;
    }

    private static AppError? FindInCreateBucket(IEnumerable<string> headerNames, Func<string, string?> header)
    {
        foreach (var name in headerNames)
        {
            if (Is(name, Acl))
            {
                if (!IsOneOf(header(name), BucketAcls))
                    return S3HeaderErrors.BucketAclNotSupported;
            }
            else if (StartsWith(name, GrantPrefix))
                return S3HeaderErrors.BucketAclNotSupported;
            else if (Is(name, BucketObjectLock))
            {
                if (!IsOneOf(header(name), [False]))
                    return S3HeaderErrors.NotImplemented(BucketObjectLock);
            }
            else if (Is(name, ObjectOwnership))
            {
                if (!IsOneOf(header(name), [OwnerEnforced]))
                    return S3HeaderErrors.NotImplemented(ObjectOwnership);
            }
        }

        return null;
    }

    private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string name, string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsOneOf(string? value, string[] allowed)
    {
        var trimmed = value?.Trim();
        foreach (var candidate in allowed)
        {
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
