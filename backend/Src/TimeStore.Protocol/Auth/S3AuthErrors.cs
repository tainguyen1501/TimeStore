using TimeStore.Primitives;

namespace TimeStore.Protocol.Auth;

/// <summary>Authentication errors on the S3 port (mapped to S3 codes by the Server's error map).</summary>
public static class S3AuthErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string AccessDenied = "S3_AUTH_ACCESS_DENIED";
        public const string SignatureMismatch = "S3_AUTH_SIGNATURE_MISMATCH";
        public const string InvalidAccessKey = "S3_AUTH_INVALID_ACCESS_KEY";
        public const string TimeSkewed = "S3_AUTH_TIME_SKEWED";
        public const string HeaderMalformed = "S3_AUTH_HEADER_MALFORMED";
        public const string QueryMalformed = "S3_AUTH_QUERY_MALFORMED";
        public const string UnsupportedMechanism = "S3_AUTH_UNSUPPORTED_MECHANISM";
        public const string InvalidToken = "S3_AUTH_INVALID_TOKEN";
        public const string MissingContentSha256 = "S3_AUTH_MISSING_CONTENT_SHA256";
        public const string InvalidContentSha256 = "S3_AUTH_INVALID_CONTENT_SHA256";
    }

    public static readonly AppError Anonymous =
        AppError.Forbidden(Codes.AccessDenied, "Anonymous access is not allowed.");

    public static readonly AppError Expired = AppError.Forbidden(Codes.AccessDenied, "Request has expired.");

    public static readonly AppError NotYetValid = AppError.Forbidden(Codes.AccessDenied, "Request is not yet valid.");

    public static readonly AppError UnsignedHeaders =
        AppError.Forbidden(Codes.AccessDenied, "There were headers present in the request which were not signed.");

    public static readonly AppError SignatureMismatch = AppError.Forbidden(
        Codes.SignatureMismatch,
        "The request signature we calculated does not match the signature you provided. Check your key and signing method.");

    public static readonly AppError InvalidAccessKey = AppError.Forbidden(
        Codes.InvalidAccessKey, "The AWS Access Key Id you provided does not exist in our records.");

    public static readonly AppError TimeSkewed = AppError.Forbidden(
        Codes.TimeSkewed, "The difference between the request time and the current time is too large.");

    public static readonly AppError UnsupportedMechanism = AppError.Validation(
        Codes.UnsupportedMechanism,
        "The authorization mechanism you have provided is not supported. Please use AWS4-HMAC-SHA256.");

    public static readonly AppError TwoMechanisms = AppError.Validation(
        Codes.UnsupportedMechanism, "Only one auth mechanism allowed; only the X-Amz-Algorithm query parameter or the Authorization header should be specified.");

    public static readonly AppError InvalidToken =
        AppError.Validation(Codes.InvalidToken, "Session tokens are not supported by this server.");

    public static readonly AppError MissingContentSha256 = AppError.Validation(
        Codes.MissingContentSha256, "Missing required header for this request: x-amz-content-sha256.");

    public static readonly AppError InvalidContentSha256 = AppError.Validation(
        Codes.InvalidContentSha256,
        "x-amz-content-sha256 must be UNSIGNED-PAYLOAD, STREAMING-AWS4-HMAC-SHA256-PAYLOAD(-TRAILER), STREAMING-UNSIGNED-PAYLOAD-TRAILER or a valid SHA-256 value.");

    public static AppError HeaderMalformed(string reason) =>
        AppError.Validation(Codes.HeaderMalformed, $"The authorization header is malformed; {reason}");

    public static AppError QueryMalformed(string reason) =>
        AppError.Validation(Codes.QueryMalformed, $"Query-string authentication is malformed; {reason}");
}
