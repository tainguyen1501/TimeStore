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

    public static AppError TooManyAttempts(TimeSpan retryAfter) => AppError.TooManyRequests(
        Codes.TooManyAttempts, $"Too many failed sign-ins. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} seconds.");
}
