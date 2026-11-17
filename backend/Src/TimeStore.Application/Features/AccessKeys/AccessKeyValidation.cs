using TimeStore.Contracts.AccessKeys;
using TimeStore.Domain.AccessKeys;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.AccessKeys;

/// <summary>Hand-written validation (AOT). Null means valid.</summary>
public static class AccessKeyValidation
{
    public static AppError? Validate(CreateAccessKeyRequest request)
    {
        var description = request.Description ?? string.Empty;
        if (description.Length > AccessKey.DescriptionMaxLength || description.Any(char.IsControl))
            return AccessKeyErrors.InvalidDescription(AccessKey.DescriptionMaxLength);
        if (request.S3Scope is not null && !AccessKeyScopes.IsValid(request.S3Scope))
            return AccessKeyErrors.InvalidScope;
        return request.Role is null || AccessKeyRoles.IsValid(request.Role) ? null : AccessKeyErrors.InvalidRole;
    }

    public static AppError? Validate(SetAccessKeyScopeRequest request) =>
        AccessKeyScopes.IsValid(request.S3Scope) ? null : AccessKeyErrors.InvalidScope;

    public static AppError? Validate(SetAccessKeyRoleRequest request) =>
        AccessKeyRoles.IsValid(request.Role) ? null : AccessKeyErrors.InvalidRole;
}
