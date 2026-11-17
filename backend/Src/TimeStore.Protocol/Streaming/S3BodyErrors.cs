using TimeStore.Primitives;

namespace TimeStore.Protocol.Streaming;

/// <summary>Request body verification failures (aws-chunked framing, chunk/trailer signatures, hashes, checksums).</summary>
public static class S3BodyErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string SignatureMismatch = "S3_BODY_SIGNATURE_MISMATCH";
        public const string BadDigest = "S3_BODY_BAD_DIGEST";
        public const string ContentSha256Mismatch = "S3_BODY_CONTENT_SHA256_MISMATCH";
        public const string Incomplete = "S3_BODY_INCOMPLETE";
        public const string Malformed = "S3_BODY_MALFORMED";
        public const string MissingDecodedLength = "S3_BODY_MISSING_DECODED_LENGTH";
        public const string InvalidTrailer = "S3_BODY_INVALID_TRAILER";
        public const string InvalidChecksum = "S3_BODY_INVALID_CHECKSUM";
    }

    public static readonly AppError ChunkSignatureMismatch = AppError.Forbidden(
        Codes.SignatureMismatch, "The chunk signature we calculated does not match the signature you provided.");

    public static readonly AppError TrailerSignatureMismatch = AppError.Forbidden(
        Codes.SignatureMismatch, "The trailer signature we calculated does not match the signature you provided.");

    public static readonly AppError BadDigest = AppError.Validation(
        Codes.BadDigest, "The checksum you specified did not match what we received.");

    public static readonly AppError ContentSha256Mismatch = AppError.Validation(
        Codes.ContentSha256Mismatch, "The provided 'x-amz-content-sha256' header does not match what was computed.");

    public static readonly AppError Incomplete = AppError.Validation(
        Codes.Incomplete, "You did not provide the number of bytes specified by the Content-Length HTTP header.");

    public static readonly AppError MissingDecodedLength = AppError.Validation(
        Codes.MissingDecodedLength, "Chunked uploads require a valid x-amz-decoded-content-length header.");

    public static AppError Malformed(string reason) =>
        AppError.Validation(Codes.Malformed, $"The aws-chunked request body is malformed: {reason}");

    public static AppError InvalidTrailer(string reason) =>
        AppError.Validation(Codes.InvalidTrailer, $"The trailing checksum is invalid: {reason}");

    public static AppError InvalidChecksum(string header) =>
        AppError.Validation(Codes.InvalidChecksum, $"Value for {header} header is invalid.");
}

/// <summary>
/// Thrown by the verifying body streams when the body breaks its integrity promise. An <see cref="IOException"/> on
/// purpose: whatever reads the body (blob store, XML reader) stops as on any I/O failure; the S3 dispatcher turns it
/// into the S3 error carried by <see cref="Error"/> before anything is committed.
/// </summary>
public sealed class S3BodyVerificationException(AppError error) : IOException(error.Message)
{
    public AppError Error { get; } = error;
}
