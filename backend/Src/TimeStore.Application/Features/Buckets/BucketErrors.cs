using TimeStore.Primitives;

namespace TimeStore.Application.Features.Buckets;

public static class BucketErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string InvalidName = "BUCKET_INVALID_NAME";
        public const string AlreadyExists = "BUCKET_ALREADY_EXISTS";
        public const string NotFound = "BUCKET_NOT_FOUND";
        public const string NotEmpty = "BUCKET_NOT_EMPTY";
        public const string InvalidVersioning = "BUCKET_INVALID_VERSIONING";
    }

    public static AppError InvalidName(string name) =>
        AppError.Validation(Codes.InvalidName, $"'{name}' is not a valid bucket name.");

    public static AppError AlreadyExists(string name) =>
        AppError.Conflict(Codes.AlreadyExists, $"Bucket '{name}' already exists.");

    public static AppError NotEmpty(string name) =>
        AppError.Conflict(Codes.NotEmpty, $"Bucket '{name}' is not empty; delete its objects first.");

    public static AppError NotFound(string name) =>
        AppError.NotFound(Codes.NotFound, $"Bucket '{name}' was not found.");

    public static readonly AppError InvalidVersioning = AppError.Validation(
        Codes.InvalidVersioning, "Versioning status must be Enabled or Suspended (a bucket never returns to unversioned).");
}
