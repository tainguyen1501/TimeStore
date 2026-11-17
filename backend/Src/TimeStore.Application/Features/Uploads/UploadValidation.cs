using TimeStore.Application.Features.Objects;
using TimeStore.Domain.Uploads;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Uploads;

/// <summary>Checks that need no I/O. Null means valid.</summary>
public static class UploadValidation
{
    private const int Md5Length = 16;

    public static AppError? ValidateCreate(CreateUploadCommand command) =>
        ObjectValidation.ValidateKeyAndMetadata(command.Key, command.UserMetadata);

    public static AppError? ValidatePart(UploadPartCommand command)
    {
        if (command.Length is not { } length)
            return ObjectErrors.LengthRequired;
        if (length > MultipartUpload.MaxPartSize)
            return UploadErrors.PartTooLarge;
        return command.ContentMd5 is { Length: not Md5Length } ? ObjectErrors.InvalidDigest : null;
    }
}
