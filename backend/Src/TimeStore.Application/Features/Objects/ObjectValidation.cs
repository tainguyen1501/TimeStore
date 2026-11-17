using System.Text;
using TimeStore.Domain.Objects;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Objects;

/// <summary>Checks that need no I/O, run before the body is read. Null means valid.</summary>
public static class ObjectValidation
{
    private const int Md5Length = 16;

    public static AppError? ValidatePut(PutObjectCommand command)
    {
        if (ValidateKeyAndMetadata(command.Key, command.UserMetadata) is { } invalid)
            return invalid;
        if (command.Length is not { } length)
            return ObjectErrors.LengthRequired;
        if (length > StoredObject.SinglePutMaxBytes)
            return ObjectErrors.TooLarge;
        if (command.ContentMd5 is { Length: not Md5Length })
            return ObjectErrors.InvalidDigest;
        return null;
    }

    /// <summary>The checks every write of an object makes (PutObject, CreateMultipartUpload).</summary>
    public static AppError? ValidateKeyAndMetadata(string key, IReadOnlyList<ObjectHeader> userMetadata)
    {
        if (!ObjectKeys.IsListable(key))
            return ObjectErrors.InvalidKey;
        return MetadataBytes(userMetadata) > StoredObject.UserMetadataMaxBytes ? ObjectErrors.MetadataTooLarge : null;
    }

    /// <summary>Size counted as Amazon S3 does: UTF-8 bytes of every name and value.</summary>
    private static int MetadataBytes(IReadOnlyList<ObjectHeader> metadata)
    {
        var total = 0;
        foreach (var entry in metadata)
            total += Encoding.UTF8.GetByteCount(entry.Name) + Encoding.UTF8.GetByteCount(entry.Value);
        return total;
    }
}
