using TimeStore.Domain.Uploads;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Uploads;

public static class UploadErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string NotFound = "UPLOAD_NOT_FOUND";
        public const string PartTooLarge = "UPLOAD_PART_TOO_LARGE";
        public const string ChecksumMismatch = "UPLOAD_CHECKSUM_MISMATCH";
        public const string InvalidMarker = "UPLOAD_INVALID_MARKER";
        public const string NoParts = "UPLOAD_NO_PARTS";
        public const string InvalidPartOrder = "UPLOAD_INVALID_PART_ORDER";
        public const string InvalidPart = "UPLOAD_INVALID_PART";
        public const string PartTooSmall = "UPLOAD_PART_TOO_SMALL";
        public const string InvalidPartNumber = "UPLOAD_INVALID_PART_NUMBER";
        public const string BadObjectChecksum = "UPLOAD_BAD_OBJECT_CHECKSUM";
        public const string CopyRangeOutOfBounds = "UPLOAD_COPY_RANGE_OUT_OF_BOUNDS";
    }

    public static readonly AppError NotFound = AppError.NotFound(
        Codes.NotFound,
        "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.");

    public static readonly AppError PartTooLarge = AppError.Validation(
        Codes.PartTooLarge, $"Your proposed upload exceeds the maximum allowed part size of {MultipartUpload.MaxPartSize} bytes.");

    public static readonly AppError InvalidMarker =
        AppError.Validation(Codes.InvalidMarker, "Invalid value for argument 'upload-id-marker'.");

    public static readonly AppError NoParts = AppError.Validation(
        Codes.NoParts, "The XML you provided was not well-formed or did not validate against our published schema.");

    public static readonly AppError InvalidPartOrder = AppError.Validation(
        Codes.InvalidPartOrder, "The list of parts was not in ascending order. Parts must be ordered by part number.");

    public static readonly AppError InvalidPartNumber = AppError.Validation(
        Codes.InvalidPartNumber,
        $"Part number must be an integer between {MultipartUpload.MinPartNumber} and {MultipartUpload.MaxPartNumber}, inclusive.");

    public static readonly AppError BadObjectChecksum = AppError.Validation(
        Codes.BadObjectChecksum, "The checksum you specified for the object did not match the checksum computed from its parts.");

    public static AppError InvalidPart(int partNumber) => AppError.Validation(
        Codes.InvalidPart,
        $"One or more of the specified parts could not be found. The part may not have been uploaded, or the specified entity tag may not match the part's entity tag (part {partNumber}).");

    public static AppError PartTooSmall(int partNumber) => AppError.Validation(
        Codes.PartTooSmall,
        $"Your proposed upload is smaller than the minimum allowed size: part {partNumber} is under {MultipartUpload.MinPartSize} bytes and is not the last part.");

    /// <summary>The part's checksum does not use the upload's algorithm (or is missing).</summary>
    public static AppError ChecksumMismatch(string? expected, string? actual) => AppError.Validation(
        Codes.ChecksumMismatch,
        $"Checksum Type mismatch occurred, expected checksum Type: {expected?.ToLowerInvariant() ?? "null"}, actual checksum Type: {actual?.ToLowerInvariant() ?? "null"}");

    /// <summary>The copy range does not fit the source object (Amazon S3: InvalidRange).</summary>
    public static AppError CopyRangeOutOfBounds(long size) => AppError.Validation(
        Codes.CopyRangeOutOfBounds, $"The specified copy source range is not valid for a source object of size {size}.");
}
