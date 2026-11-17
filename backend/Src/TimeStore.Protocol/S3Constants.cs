namespace TimeStore.Protocol;

/// <summary>Amazon S3 error codes (the <c>Code</c> element of an error response).</summary>
public static class S3ErrorCodes
{
    public const string AccessDenied = "AccessDenied";
    public const string AuthorizationHeaderMalformed = "AuthorizationHeaderMalformed";
    public const string AuthorizationQueryParametersError = "AuthorizationQueryParametersError";
    public const string InvalidAccessKeyId = "InvalidAccessKeyId";
    public const string InvalidToken = "InvalidToken";
    public const string RequestTimeTooSkewed = "RequestTimeTooSkewed";
    public const string BadDigest = "BadDigest";
    public const string BucketAlreadyOwnedByYou = "BucketAlreadyOwnedByYou";
    public const string BucketNotEmpty = "BucketNotEmpty";
    public const string IncompleteBody = "IncompleteBody";
    public const string EntityTooLarge = "EntityTooLarge";
    public const string MetadataTooLarge = "MetadataTooLarge";
    public const string InvalidDigest = "InvalidDigest";
    public const string InvalidRange = "InvalidRange";
    public const string InvalidPartNumber = "InvalidPartNumber";
    public const string NoSuchVersion = "NoSuchVersion";
    public const string NoSuchLifecycleConfiguration = "NoSuchLifecycleConfiguration";
    public const string NoSuchBucketPolicy = "NoSuchBucketPolicy";
    public const string MalformedPolicy = "MalformedPolicy";
    public const string InvalidStorageClass = "InvalidStorageClass";
    public const string MissingContentLength = "MissingContentLength";
    public const string MalformedXml = "MalformedXML";
    public const string InvalidLocationConstraint = "InvalidLocationConstraint";
    public const string XAmzContentSha256Mismatch = "XAmzContentSHA256Mismatch";
    public const string InternalError = "InternalError";
    public const string InvalidArgument = "InvalidArgument";
    public const string InvalidBucketName = "InvalidBucketName";
    public const string InvalidRequest = "InvalidRequest";
    public const string InvalidUri = "InvalidURI";
    public const string KeyTooLongError = "KeyTooLongError";
    public const string MethodNotAllowed = "MethodNotAllowed";
    public const string NoSuchBucket = "NoSuchBucket";
    public const string NoSuchKey = "NoSuchKey";
    public const string NoSuchUpload = "NoSuchUpload";
    public const string InvalidPart = "InvalidPart";
    public const string InvalidPartOrder = "InvalidPartOrder";
    public const string EntityTooSmall = "EntityTooSmall";
    public const string AccessControlListNotSupported = "AccessControlListNotSupported";
    public const string InvalidBucketAclWithObjectOwnership = "InvalidBucketAclWithObjectOwnership";
    public const string NotImplemented = "NotImplemented";
    public const string OperationAborted = "OperationAborted";
    public const string PreconditionFailed = "PreconditionFailed";
    public const string SignatureDoesNotMatch = "SignatureDoesNotMatch";
}

/// <summary>Element names of S3 XML documents.</summary>
public static class S3Xml
{
    /// <summary>Namespace of S3 response documents (error documents have none).</summary>
    public const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>S3 timestamps in XML: ISO 8601, UTC, milliseconds.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public const string Error = "Error";
    public const string Code = "Code";
    public const string Message = "Message";
    public const string Resource = "Resource";
    public const string RequestId = "RequestId";
}

public static class S3ContentTypes
{
    public const string Xml = "application/xml";
}

public static class S3Messages
{
    public const string NotImplemented = "A header or operation you provided implies functionality that is not implemented.";
    public const string NoSuchBucket = "The specified bucket does not exist.";
    public const string InvalidRange = "The requested range is not satisfiable.";
    public const string InvalidPartNumber = "The requested partnumber is not satisfiable.";
    public const string RangeWithPartNumber = "Cannot specify both Range header and partNumber query parameter.";
    public const string PreconditionFailed = "At least one of the pre-conditions you specified did not hold.";
    public const string NoSuchVersion = "The specified version does not exist.";
    public const string MethodNotAllowed = "The specified method is not allowed against this resource.";
    public const string KeyTooLong = "Your key is too long.";
    public const string InternalError = "We encountered an internal error. Please try again.";
}
