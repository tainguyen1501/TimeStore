using TimeStore.Contracts.AccessKeys;
using TimeStore.Domain.AccessKeys;

namespace TimeStore.Application.Features.AccessKeys;

public static class AccessKeyMappings
{
    extension(AccessKey key)
    {
        /// <summary>Never carries the secret.</summary>
        public AccessKeyResponse ToResponse() => new(key.AccessKeyId, key.Description, key.Role, key.CreatedAt, key.DisabledAt, key.S3Scope);
    }
}
