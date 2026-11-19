using TimeStore.Primitives;

namespace TimeStore.Application.Features.Auth;

public static class AuthErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string InvalidCredentials = "AUTH_INVALID_CREDENTIALS";
        public const string TooManyAttempts = "AUTH_TOO_MANY_ATTEMPTS";
        public const string Unauthenticated = "AUTH_UNAUTHENTICATED";
        public const string Forbidden = "AUTH_FORBIDDEN";
        public const string CrossSite = "AUTH_CROSS_SITE";
        public const string SignatureRequired = "AUTH_SIGNATURE_REQUIRED";
        public const string PayloadMismatch = "AUTH_PAYLOAD_MISMATCH";
    }

    /// <summary>One answer for every failure, so a caller cannot tell which keys exist or which are admins.</summary>
    public static readonly AppError InvalidCredentials = AppError.Unauthorized(
        Codes.InvalidCredentials, "The access key or secret key is not valid, or the key may not sign in.");

    public static readonly AppError Unauthenticated =
        AppError.Unauthorized(Codes.Unauthenticated, "Sign in to use the admin API.");

    public static readonly AppError Forbidden =
        AppError.Forbidden(Codes.Forbidden, "Your role does not allow this operation.");

    public static readonly AppError CrossSite = AppError.Forbidden(
        Codes.CrossSite, "State-changing requests must come from the same origin.");

    public static readonly AppError SignatureRequired = AppError.Unauthorized(Codes.SignatureRequired,
        "This API is for programs: sign each request with AWS Signature Version 4 (an access key with the admin or "
        + "backup role). Sign-in cookies are not accepted.");

    public static readonly AppError PayloadMismatch = AppError.Validation(Codes.PayloadMismatch,
        "The request body does not match x-amz-content-sha256, is too large, or uses a streaming payload this API does not take.");

    public static AppError TooManyAttempts(TimeSpan retryAfter) => AppError.TooManyRequests(
        Codes.TooManyAttempts, $"Too many failed sign-ins. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} seconds.");
}
