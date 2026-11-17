using TimeStore.Primitives;

namespace TimeStore.Application.Common;

/// <summary>Errors shared by every feature.</summary>
public static class CommonErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string Internal = "INTERNAL_ERROR";
        public const string InvalidRequest = "COMMON_INVALID_REQUEST";
    }

    /// <summary>The request body or parameters could not be read (malformed JSON, missing body).</summary>
    public static readonly AppError InvalidRequest =
        AppError.Validation(Codes.InvalidRequest, "The request is malformed or incomplete.");

    /// <summary>Answer to an unexpected exception; details go to the log only.</summary>
    public static readonly AppError Internal = AppError.Internal(Codes.Internal, "An unexpected error occurred.");
}
