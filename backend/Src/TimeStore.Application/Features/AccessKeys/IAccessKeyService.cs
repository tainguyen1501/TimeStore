using TimeStore.Contracts.AccessKeys;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.AccessKeys;

/// <summary>
/// Access keys stored in the metadata database (admin API). The root key from configuration is a break-glass key: it
/// is not stored, never listed, and cannot be disabled or deleted here.
/// </summary>
public interface IAccessKeyService
{
    /// <summary>Creates a key; the response carries the secret, which is never returned again.</summary>
    ValueTask<Result<CreatedAccessKeyResponse>> CreateAsync(CreateAccessKeyRequest request, CancellationToken ct);

    ValueTask<IReadOnlyList<AccessKeyResponse>> ListAsync(CancellationToken ct);

    /// <summary>
    /// A disabled key stays stored but fails authentication (<c>InvalidAccessKeyId</c>) at once, and its admin
    /// sessions end. Deleting a key ends them too.
    /// </summary>
    ValueTask<Result<AccessKeyResponse>> DisableAsync(string accessKeyId, CancellationToken ct);

    ValueTask<Result<AccessKeyResponse>> EnableAsync(string accessKeyId, CancellationToken ct);

    ValueTask<Result> DeleteAsync(string accessKeyId, CancellationToken ct);

    /// <summary>Changes the key's admin API role; its sessions end (they sign in again with the new role).</summary>
    ValueTask<Result<AccessKeyResponse>> SetRoleAsync(string accessKeyId, SetAccessKeyRoleRequest request, CancellationToken ct);

    /// <summary>Changes the key's S3 scope; S3 requests see it from the next request on.</summary>
    ValueTask<Result<AccessKeyResponse>> SetScopeAsync(string accessKeyId, SetAccessKeyScopeRequest request, CancellationToken ct);
}
