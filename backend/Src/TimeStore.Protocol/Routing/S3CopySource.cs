using TimeStore.Primitives;

namespace TimeStore.Protocol.Routing;

/// <summary>A parsed <c>x-amz-copy-source</c>: <c>[/]bucket/key[?versionId=id]</c>, percent-encoded.</summary>
/// <param name="VersionId">The requested version, or null.</param>
public sealed record S3CopySource(string Bucket, string Key, string? VersionId)
{
    private const string VersionIdQuery = "?versionId=";

    public static Result<S3CopySource> Parse(string raw)
    {
        var text = raw.AsSpan().Trim();
        string? versionId = null;
        if (text.IndexOf(VersionIdQuery, StringComparison.Ordinal) is var query and >= 0)
        {
            versionId = text[(query + VersionIdQuery.Length)..].ToString();
            text = text[..query];
        }

        // Clients encode either the key only (`bucket/folder%2Fa`) or the whole value, separator included
        // (`bucket%2Ffolder%2Fa`, the AWS SDK for .NET): decode everything first, then split at the first slash
        // (bucket names never contain one).
        if (!S3PercentDecoder.TryDecode(text, plusAsSpace: false, out var decoded))
            return Invalid;
        var path = decoded.AsSpan();
        if (path.StartsWith("/"))
            path = path[1..];
        var slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
            return Invalid;

        var bucket = path[..slash].ToString();
        var key = path[(slash + 1)..].ToString();
        if (S3PercentDecoder.Utf8Length(key) > S3Limits.MaxKeyBytes)
            return S3RequestErrors.KeyTooLong;
        return new S3CopySource(bucket, key, string.IsNullOrEmpty(versionId) ? null : versionId);
    }

    private static readonly AppError Invalid = AppError.Validation(
        S3RequestErrors.Codes.InvalidArgument, "Copy Source must mention the source bucket and key: sourcebucket/sourcekey.");
}
