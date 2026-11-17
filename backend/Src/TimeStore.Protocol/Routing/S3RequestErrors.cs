using TimeStore.Primitives;

namespace TimeStore.Protocol.Routing;

/// <summary>Errors found while reading an S3 request line (before authentication or any service call).</summary>
public static class S3RequestErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string InvalidUri = "S3_INVALID_URI";
        public const string MethodNotAllowed = "S3_METHOD_NOT_ALLOWED";
        public const string InvalidArgument = "S3_INVALID_ARGUMENT";
        public const string InvalidRequest = "S3_INVALID_REQUEST";
        public const string KeyTooLong = "S3_KEY_TOO_LONG";
    }

    public static readonly AppError InvalidUri =
        AppError.Validation(Codes.InvalidUri, "Couldn't parse the specified URI.");

    public static readonly AppError MethodNotAllowed =
        AppError.Validation(Codes.MethodNotAllowed, "The specified method is not allowed against this resource.");

    public static readonly AppError AmbiguousSubresource =
        AppError.Validation(Codes.InvalidRequest, "The request selects more than one operation.");

    public static readonly AppError KeyTooLong =
        AppError.Validation(Codes.KeyTooLong, "Your key is too long.");

    public static AppError InvalidArgument(string name) =>
        AppError.Validation(Codes.InvalidArgument, $"Invalid value for argument '{name}'.");

    public static AppError InvalidRequest(string message) => AppError.Validation(Codes.InvalidRequest, message);

    public static AppError MissingArgument(string name) =>
        AppError.Validation(Codes.InvalidArgument, $"Argument '{name}' is required for this request.");
}
