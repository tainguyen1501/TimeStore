namespace TimeStore.Protocol.Auth;

/// <summary>Names and constants of AWS Signature Version 4 as used by Amazon S3.</summary>
public static class SigV4Names
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string ChunkAlgorithm = "AWS4-HMAC-SHA256-PAYLOAD";
    public const string TrailerAlgorithm = "AWS4-HMAC-SHA256-TRAILER";
    public const string Terminator = "aws4_request";
    public const string Service = "s3";
    public const string SecretPrefix = "AWS4";
    public const string SigV2Prefix = "AWS ";

    // Headers (lower case).
    public const string Authorization = "authorization";
    public const string AmzDate = "x-amz-date";
    public const string Date = "date";
    public const string Host = "host";
    public const string ContentSha256 = "x-amz-content-sha256";
    public const string SecurityToken = "x-amz-security-token";
    public const string AmzPrefix = "x-amz-";

    // Authorization header parts.
    public const string CredentialPart = "Credential=";
    public const string SignedHeadersPart = "SignedHeaders=";
    public const string SignaturePart = "Signature=";

    // Presigned URL query parameters.
    public const string QueryAlgorithm = "X-Amz-Algorithm";
    public const string QueryCredential = "X-Amz-Credential";
    public const string QueryDate = "X-Amz-Date";
    public const string QueryExpires = "X-Amz-Expires";
    public const string QuerySignedHeaders = "X-Amz-SignedHeaders";
    public const string QuerySignature = "X-Amz-Signature";
    public const string QueryContentSha256 = "X-Amz-Content-Sha256";
    public const string QuerySecurityToken = "X-Amz-Security-Token";
    public const string QuerySigV2Signature = "Signature";
    public const string QuerySigV2AccessKey = "AWSAccessKeyId";

    // x-amz-content-sha256 values.
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string StreamingSigned = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";
    public const string StreamingSignedTrailer = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER";
    public const string StreamingUnsignedTrailer = "STREAMING-UNSIGNED-PAYLOAD-TRAILER";
    public const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    // Formats.
    public const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    public const string DateFormat = "yyyyMMdd";
    public const string HttpDateFormat = "r";

    public const int MaxPresignExpiresSeconds = 604_800;
    public const int SignatureHexLength = 64;
}

/// <summary>How the request body is protected (value of <c>x-amz-content-sha256</c>).</summary>
public enum PayloadMode
{
    /// <summary>The header carries the hex SHA-256 of the whole body; the body must be checked against it.</summary>
    Sha256,

    /// <summary><c>UNSIGNED-PAYLOAD</c> (also every presigned URL without <c>X-Amz-Content-Sha256</c>).</summary>
    Unsigned,

    /// <summary><c>aws-chunked</c>, every chunk signed.</summary>
    StreamingSigned,

    /// <summary><c>aws-chunked</c>, every chunk and the checksum trailer signed.</summary>
    StreamingSignedTrailer,

    /// <summary><c>aws-chunked</c> without chunk signatures, checksum trailer (current AWS SDK default).</summary>
    StreamingUnsignedTrailer,
}
