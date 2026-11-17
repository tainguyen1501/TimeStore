using TimeStore.Application.Features.Lifecycle;
using System.Collections.Frozen;
using TimeStore.Application.Common;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Primitives;
using TimeStore.Protocol;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Http;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;
using TimeStore.Protocol.Xml;

namespace TimeStore.Server.S3;

/// <summary>An S3 error as the client sees it: the S3 error code and the HTTP status.</summary>
internal readonly record struct S3ErrorInfo(string Code, int Status);

/// <summary>
/// Maps every <see cref="AppError"/> code used on the S3 port to an S3 error code and status. Protocol and service
/// catalogs are listed here; an unmapped code falls back by <see cref="ErrorKind"/>.
/// </summary>
internal static class S3ErrorMap
{
    private static readonly FrozenDictionary<string, S3ErrorInfo> Map = new Dictionary<string, S3ErrorInfo>
    {
        [S3RequestErrors.Codes.InvalidUri] = new(S3ErrorCodes.InvalidUri, StatusCodes.Status400BadRequest),
        [S3RequestErrors.Codes.MethodNotAllowed] = new(S3ErrorCodes.MethodNotAllowed, StatusCodes.Status405MethodNotAllowed),
        [S3RequestErrors.Codes.InvalidArgument] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [S3RequestErrors.Codes.InvalidRequest] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [S3RequestErrors.Codes.KeyTooLong] = new(S3ErrorCodes.KeyTooLongError, StatusCodes.Status400BadRequest),
        [S3AuthErrors.Codes.AccessDenied] = new(S3ErrorCodes.AccessDenied, StatusCodes.Status403Forbidden),
        [S3AuthErrors.Codes.SignatureMismatch] = new(S3ErrorCodes.SignatureDoesNotMatch, StatusCodes.Status403Forbidden),
        [S3AuthErrors.Codes.InvalidAccessKey] = new(S3ErrorCodes.InvalidAccessKeyId, StatusCodes.Status403Forbidden),
        [S3AuthErrors.Codes.TimeSkewed] = new(S3ErrorCodes.RequestTimeTooSkewed, StatusCodes.Status403Forbidden),
        [S3AuthErrors.Codes.HeaderMalformed] = new(S3ErrorCodes.AuthorizationHeaderMalformed, StatusCodes.Status400BadRequest),
        [S3AuthErrors.Codes.QueryMalformed] = new(S3ErrorCodes.AuthorizationQueryParametersError, StatusCodes.Status400BadRequest),
        [S3AuthErrors.Codes.UnsupportedMechanism] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [S3AuthErrors.Codes.InvalidToken] = new(S3ErrorCodes.InvalidToken, StatusCodes.Status400BadRequest),
        [S3AuthErrors.Codes.MissingContentSha256] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [S3AuthErrors.Codes.InvalidContentSha256] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [S3BodyErrors.Codes.SignatureMismatch] = new(S3ErrorCodes.SignatureDoesNotMatch, StatusCodes.Status403Forbidden),
        [S3BodyErrors.Codes.BadDigest] = new(S3ErrorCodes.BadDigest, StatusCodes.Status400BadRequest),
        [S3BodyErrors.Codes.ContentSha256Mismatch] = new(S3ErrorCodes.XAmzContentSha256Mismatch, StatusCodes.Status400BadRequest),
        [S3BodyErrors.Codes.Incomplete] = new(S3ErrorCodes.IncompleteBody, StatusCodes.Status400BadRequest),
        [S3BodyErrors.Codes.Malformed] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [S3BodyErrors.Codes.MissingDecodedLength] = new(S3ErrorCodes.MissingContentLength, StatusCodes.Status411LengthRequired),
        [S3BodyErrors.Codes.InvalidTrailer] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [S3BodyErrors.Codes.InvalidChecksum] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [S3XmlErrors.Codes.MalformedXml] = new(S3ErrorCodes.MalformedXml, StatusCodes.Status400BadRequest),
        [S3XmlErrors.Codes.InvalidLocationConstraint] = new(S3ErrorCodes.InvalidLocationConstraint, StatusCodes.Status400BadRequest),
        [BucketErrors.Codes.InvalidName] = new(S3ErrorCodes.InvalidBucketName, StatusCodes.Status400BadRequest),
        [BucketErrors.Codes.AlreadyExists] = new(S3ErrorCodes.BucketAlreadyOwnedByYou, StatusCodes.Status409Conflict),
        [BucketErrors.Codes.NotFound] = new(S3ErrorCodes.NoSuchBucket, StatusCodes.Status404NotFound),
        [BucketErrors.Codes.NotEmpty] = new(S3ErrorCodes.BucketNotEmpty, StatusCodes.Status409Conflict),
        [ObjectErrors.Codes.InvalidKey] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.LengthRequired] = new(S3ErrorCodes.MissingContentLength, StatusCodes.Status411LengthRequired),
        [ObjectErrors.Codes.TooLarge] = new(S3ErrorCodes.EntityTooLarge, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.MetadataTooLarge] = new(S3ErrorCodes.MetadataTooLarge, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.InvalidDigest] = new(S3ErrorCodes.InvalidDigest, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.BadDigest] = new(S3ErrorCodes.BadDigest, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.AlreadyExists] = new(S3ErrorCodes.PreconditionFailed, StatusCodes.Status412PreconditionFailed),
        [ObjectErrors.Codes.NotFound] = new(S3ErrorCodes.NoSuchKey, StatusCodes.Status404NotFound),
        [BucketErrors.Codes.InvalidVersioning] = new(S3ErrorCodes.MalformedXml, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.CopyRangeOutOfBounds] = new(S3ErrorCodes.InvalidRange, StatusCodes.Status416RangeNotSatisfiable),
        [Application.Features.Policies.PolicyErrors.Codes.Malformed] = new(S3ErrorCodes.MalformedPolicy, StatusCodes.Status400BadRequest),
        [Application.Features.Policies.PolicyErrors.Codes.NotFound] = new(S3ErrorCodes.NoSuchBucketPolicy, StatusCodes.Status404NotFound),
        [Application.Features.Policies.PolicyErrors.Codes.NotImplemented] = new(S3ErrorCodes.NotImplemented, StatusCodes.Status501NotImplemented),
        [Application.Features.Policies.PolicyErrors.Codes.InvalidPublicPrefix] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest), // admin API only
        [Application.Features.Policies.PolicyErrors.Codes.Conflict] = new(S3ErrorCodes.OperationAborted, StatusCodes.Status409Conflict), // admin API only
        [Application.Features.Policies.PolicyErrors.Codes.AccessDenied] = new(S3ErrorCodes.AccessDenied, StatusCodes.Status403Forbidden),
        [LifecycleErrors.Codes.NotFound] = new(S3ErrorCodes.NoSuchLifecycleConfiguration, StatusCodes.Status404NotFound),
        [LifecycleErrors.Codes.Invalid] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [LifecycleErrors.Codes.Malformed] = new(S3ErrorCodes.MalformedXml, StatusCodes.Status400BadRequest),
        [LifecycleErrors.Codes.NotImplemented] = new(S3ErrorCodes.NotImplemented, StatusCodes.Status501NotImplemented),
        [LifecycleErrors.Codes.StorageClass] = new(S3ErrorCodes.InvalidStorageClass, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.InvalidVersionId] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.InvalidShareExpiry] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest), // admin API only
        [ObjectErrors.Codes.NoSuchVersion] = new(S3ErrorCodes.NoSuchVersion, StatusCodes.Status404NotFound),
        [ObjectErrors.Codes.CopySourceIsDeleteMarker] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.IntegrityRequired] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.TooManyKeys] = new(S3ErrorCodes.MalformedXml, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.CopyToItself] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [ObjectErrors.Codes.CopySourceTooLarge] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.NotFound] = new(S3ErrorCodes.NoSuchUpload, StatusCodes.Status404NotFound),
        [UploadErrors.Codes.PartTooLarge] = new(S3ErrorCodes.EntityTooLarge, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.ChecksumMismatch] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.InvalidMarker] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.NoParts] = new(S3ErrorCodes.MalformedXml, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.InvalidPartOrder] = new(S3ErrorCodes.InvalidPartOrder, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.InvalidPart] = new(S3ErrorCodes.InvalidPart, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.PartTooSmall] = new(S3ErrorCodes.EntityTooSmall, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.InvalidPartNumber] = new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
        [UploadErrors.Codes.BadObjectChecksum] = new(S3ErrorCodes.BadDigest, StatusCodes.Status400BadRequest),
        [S3HeaderErrors.Codes.NotImplemented] = new(S3ErrorCodes.NotImplemented, StatusCodes.Status501NotImplemented),
        [S3HeaderErrors.Codes.AclNotSupported] = new(S3ErrorCodes.AccessControlListNotSupported, StatusCodes.Status400BadRequest),
        [S3HeaderErrors.Codes.BucketAclNotSupported] = new(S3ErrorCodes.InvalidBucketAclWithObjectOwnership, StatusCodes.Status400BadRequest),
        [CommonErrors.Codes.Internal] = new(S3ErrorCodes.InternalError, StatusCodes.Status500InternalServerError),
        [CommonErrors.Codes.InvalidRequest] = new(S3ErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>True when <paramref name="code"/> has an explicit S3 mapping (guarded by a convention test).</summary>
    public static bool IsMapped(string code) => Map.ContainsKey(code);

    public static S3ErrorInfo Resolve(AppError error) =>
        Map.TryGetValue(error.Code, out var info) ? info : error.Kind switch
        {
            ErrorKind.Validation => new(S3ErrorCodes.InvalidArgument, StatusCodes.Status400BadRequest),
            ErrorKind.Forbidden => new(S3ErrorCodes.AccessDenied, StatusCodes.Status403Forbidden),
            ErrorKind.PreconditionFailed => new(S3ErrorCodes.PreconditionFailed, StatusCodes.Status412PreconditionFailed),
            _ => new(S3ErrorCodes.InternalError, StatusCodes.Status500InternalServerError),
        };
}

/// <summary>Writes S3 error responses (XML body; status only for HEAD).</summary>
internal static class S3Errors
{
    private const string ExpectContinue = "100-continue";
    private const string ConnectionClose = "close";

    public static Task WriteAsync(HttpContext context, AppError error)
    {
        var info = S3ErrorMap.Resolve(error);
        return WriteAsync(context, info.Code, error.Message, info.Status);
    }

    public static Task WriteAsync(HttpContext context, string s3Code, string message, int status)
    {
        context.Response.StatusCode = status;

        // A client that sent "Expect: 100-continue" may still send its body after an early error answer. On a reused
        // connection those bytes would be read as the next request; closing the connection after the error avoids it.
        if (context.Request.Headers.Expect.ToString().Contains(ExpectContinue, StringComparison.OrdinalIgnoreCase))
            context.Response.Headers.Connection = ConnectionClose;
        if (HttpMethods.IsHead(context.Request.Method))
            return Task.CompletedTask;

        context.Response.ContentType = S3ContentTypes.Xml;
        return S3ErrorXml.WriteAsync(
            context.Response.Body, s3Code, message, context.Request.Path.Value ?? string.Empty, context.TraceIdentifier);
    }
}
