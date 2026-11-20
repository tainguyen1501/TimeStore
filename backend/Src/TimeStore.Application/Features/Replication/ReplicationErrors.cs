using TimeStore.Primitives;

namespace TimeStore.Application.Features.Replication;

public static class ReplicationErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string NotFound = "REPLICATION_NOT_FOUND";
        public const string InvalidEndpoint = "REPLICATION_INVALID_ENDPOINT";
        public const string InvalidDestination = "REPLICATION_INVALID_DESTINATION";
        public const string SecretRequired = "REPLICATION_SECRET_REQUIRED";
        public const string DestinationFailed = "REPLICATION_DESTINATION_FAILED";
        public const string Disabled = "REPLICATION_DISABLED";
    }

    public static AppError NotFound(string bucket) =>
        AppError.NotFound(Codes.NotFound, $"Bucket '{bucket}' has no replication rule.");

    public static readonly AppError InvalidEndpoint = AppError.Validation(Codes.InvalidEndpoint,
        "The endpoint must be an http or https URL with a host and an optional port only (no path, query or user info).");

    public static AppError InvalidDestination(string reason) => AppError.Validation(Codes.InvalidDestination, reason);

    public static readonly AppError SecretRequired = AppError.Validation(Codes.SecretRequired,
        "A secret key is required for a new rule, or when the endpoint or the access key changes.");

    /// <summary>The destination refused the test write or could not be reached (its S3 error code, never a secret).</summary>
    public static AppError DestinationFailed(string reason) => AppError.Conflict(Codes.DestinationFailed,
        $"The destination did not accept a test object: {reason}");

    public static readonly AppError Disabled = AppError.Conflict(Codes.Disabled,
        "Replication is turned off on this server (TimeStore:Replication:Enabled).");
}
