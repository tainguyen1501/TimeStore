using TimeStore.Domain.Objects;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Objects;

public static class ObjectErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string InvalidKey = "OBJECT_INVALID_KEY";
        public const string LengthRequired = "OBJECT_LENGTH_REQUIRED";
        public const string TooLarge = "OBJECT_TOO_LARGE";
        public const string MetadataTooLarge = "OBJECT_METADATA_TOO_LARGE";
        public const string InvalidDigest = "OBJECT_INVALID_DIGEST";
        public const string BadDigest = "OBJECT_BAD_DIGEST";
        public const string AlreadyExists = "OBJECT_ALREADY_EXISTS";
        public const string NotFound = "OBJECT_NOT_FOUND";
        public const string IntegrityRequired = "OBJECT_INTEGRITY_REQUIRED";
        public const string TooManyKeys = "OBJECT_TOO_MANY_KEYS";
        public const string CopyToItself = "OBJECT_COPY_TO_ITSELF";
        public const string CopySourceTooLarge = "OBJECT_COPY_SOURCE_TOO_LARGE";
        public const string InvalidVersionId = "OBJECT_INVALID_VERSION_ID";
        public const string NoSuchVersion = "OBJECT_NO_SUCH_VERSION";
        public const string CopySourceIsDeleteMarker = "OBJECT_COPY_SOURCE_DELETE_MARKER";
        public const string InvalidShareExpiry = "OBJECT_INVALID_SHARE_EXPIRY";
    }

    /// <summary>Share links (admin API) live 1 second to 7 days, Amazon S3's limit for presigned URLs.</summary>
    public static AppError InvalidShareExpiry(int maxSeconds) => AppError.Validation(
        Codes.InvalidShareExpiry, $"A share link must expire after 1 to {maxSeconds} seconds.");

    public static readonly AppError InvalidKey = AppError.Validation(
        Codes.InvalidKey, "Object keys cannot contain control characters (they could not be listed).");

    public static readonly AppError LengthRequired =
        AppError.Validation(Codes.LengthRequired, "You must provide the Content-Length HTTP header.");

    public static readonly AppError TooLarge = AppError.Validation(
        Codes.TooLarge, $"Your proposed upload exceeds the maximum allowed size of {StoredObject.SinglePutMaxBytes} bytes; use a multipart upload.");

    public static readonly AppError MetadataTooLarge = AppError.Validation(
        Codes.MetadataTooLarge, $"Your metadata headers exceed the maximum allowed metadata size of {StoredObject.UserMetadataMaxBytes} bytes.");

    public static readonly AppError InvalidDigest =
        AppError.Validation(Codes.InvalidDigest, "The Content-MD5 you specified was invalid.");

    public static readonly AppError BadDigest =
        AppError.Validation(Codes.BadDigest, "The Content-MD5 you specified did not match what we received.");

    public static readonly AppError IntegrityRequired = AppError.Validation(
        Codes.IntegrityRequired, "Missing required header for this request: Content-MD5 or x-amz-checksum-*.");

    public static readonly AppError TooManyKeys = AppError.Validation(
        Codes.TooManyKeys, $"A multi-object delete accepts at most {MaxDeleteKeys} keys.");

    public const int MaxDeleteKeys = 1000;

    /// <summary>Largest copy source (Amazon S3 limit: larger objects are copied part by part with UploadPartCopy).</summary>
    public const long MaxCopySourceBytes = StoredObject.SinglePutMaxBytes;

    public static readonly AppError CopyToItself = AppError.Validation(
        Codes.CopyToItself,
        "This copy request is illegal because it is trying to copy an object to itself without changing the object's metadata, storage class, website redirect location or encryption attributes.");

    public static readonly AppError CopySourceTooLarge = AppError.Validation(
        Codes.CopySourceTooLarge,
        $"The specified copy source is larger than the maximum allowable size for a copy source: {MaxCopySourceBytes}");

    public static AppError AlreadyExists(string key) => AppError.PreconditionFailed(
        Codes.AlreadyExists, $"At least one of the pre-conditions you specified did not hold: '{key}' already exists.");

    public static AppError NotFound(string key) =>
        AppError.NotFound(Codes.NotFound, $"The specified key '{key}' does not exist.");

    public static readonly AppError InvalidVersionId =
        AppError.Validation(Codes.InvalidVersionId, "Invalid version id specified");

    public static readonly AppError NoSuchVersion =
        AppError.NotFound(Codes.NoSuchVersion, "The specified version does not exist.");

    public static readonly AppError CopySourceIsDeleteMarker = AppError.Validation(
        Codes.CopySourceIsDeleteMarker, "The source of a copy request may not specifically refer to a delete marker by version id.");
}
