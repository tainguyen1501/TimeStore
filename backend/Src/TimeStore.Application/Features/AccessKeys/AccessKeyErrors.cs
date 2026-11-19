using TimeStore.Domain.AccessKeys;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.AccessKeys;

public static class AccessKeyErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string InvalidDescription = "ACCESS_KEY_INVALID_DESCRIPTION";
        public const string NotFound = "ACCESS_KEY_NOT_FOUND";
        public const string InvalidRole = "ACCESS_KEY_INVALID_ROLE";
        public const string InvalidScope = "ACCESS_KEY_INVALID_SCOPE";
    }

    public static AppError InvalidDescription(int maxLength) =>
        AppError.Validation(
            Codes.InvalidDescription, $"The description must be at most {maxLength} characters without control characters.");

    public static readonly AppError InvalidScope = AppError.Validation(
        Codes.InvalidScope, $"The S3 scope must be {AccessKeyScopes.Full} or {AccessKeyScopes.Policy}.");

    public static readonly AppError InvalidRole = AppError.Validation(
        Codes.InvalidRole, $"The role must be {AccessKeyRoles.None}, {AccessKeyRoles.Viewer}, {AccessKeyRoles.Admin} or {AccessKeyRoles.Backup}.");

    // The id is echoed: it is not a secret (it travels in every request's Authorization header).
    public static AppError NotFound(string accessKeyId) =>
        AppError.NotFound(Codes.NotFound, $"Access key '{accessKeyId}' was not found.");
}
