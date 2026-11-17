using System.Text;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.AccessKeys;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Stores;
using TimeStore.Infra.Credentials;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.AccessKeys;

public sealed class AccessKeyService(
    IMetadataStore store, ISecretProtector protector, ICacheService cache, TimeProvider clock) : IAccessKeyService
{
    // 90 random bits make a collision practically impossible; the retry only keeps a freak collision from failing.
    private const int MaxCreateAttempts = 3;

    public async ValueTask<Result<CreatedAccessKeyResponse>> CreateAsync(CreateAccessKeyRequest request, CancellationToken ct)
    {
        if (AccessKeyValidation.Validate(request) is { } invalid)
            return invalid;

        var description = request.Description ?? string.Empty;
        var role = request.Role ?? AccessKeyRoles.None;
        for (var attempt = 1; ; attempt++)
        {
            var accessKeyId = AccessKeyGenerator.NewAccessKeyId();
            var secret = AccessKeyGenerator.NewSecretKey();
            var ciphertext = protector.Protect(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(accessKeyId));
            var key = new AccessKey(accessKeyId, ciphertext, description, role, clock.UtcNowMs, DisabledAt: null)
            {
                S3Scope = request.S3Scope ?? AccessKeyScopes.Full,
            };
            if (await store.TryCreateAccessKeyAsync(key, ct))
                return new CreatedAccessKeyResponse(accessKeyId, secret, description, role, key.CreatedAt, key.S3Scope);
            if (attempt == MaxCreateAttempts)
                throw new InvalidOperationException("Could not generate a free access key id.");
        }
    }

    public async ValueTask<IReadOnlyList<AccessKeyResponse>> ListAsync(CancellationToken ct)
    {
        var keys = await store.ListAccessKeysAsync(ct);
        var responses = new AccessKeyResponse[keys.Count];
        for (var i = 0; i < responses.Length; i++)
            responses[i] = keys[i].ToResponse();
        return responses;
    }

    public ValueTask<Result<AccessKeyResponse>> DisableAsync(string accessKeyId, CancellationToken ct) =>
        SetDisabledAsync(accessKeyId, clock.UtcNowMs, ct);

    public ValueTask<Result<AccessKeyResponse>> EnableAsync(string accessKeyId, CancellationToken ct) =>
        SetDisabledAsync(accessKeyId, disabledAt: null, ct);

    public async ValueTask<Result> DeleteAsync(string accessKeyId, CancellationToken ct)
    {
        var deleted = await store.DeleteAccessKeyAsync(accessKeyId, ct);
        await cache.RemoveAsync(CredentialCache.KeyFor(accessKeyId), ct);
        await store.DeleteSessionsOfAsync(accessKeyId, ct);
        return deleted ? Result.Success : AccessKeyErrors.NotFound(accessKeyId);
    }

    public async ValueTask<Result<AccessKeyResponse>> SetRoleAsync(
        string accessKeyId, SetAccessKeyRoleRequest request, CancellationToken ct)
    {
        if (AccessKeyValidation.Validate(request) is { } invalid)
            return invalid;
        if (!await store.SetAccessKeyRoleAsync(accessKeyId, request.Role, ct)
            || await store.FindAccessKeyAsync(accessKeyId, ct) is not { } key)
            return AccessKeyErrors.NotFound(accessKeyId);
        await store.DeleteSessionsOfAsync(accessKeyId, ct); // the new role applies from the next sign-in
        return key.ToResponse();
    }

    public async ValueTask<Result<AccessKeyResponse>> SetScopeAsync(
        string accessKeyId, SetAccessKeyScopeRequest request, CancellationToken ct)
    {
        if (AccessKeyValidation.Validate(request) is { } invalid)
            return invalid;
        if (!await store.SetAccessKeyScopeAsync(accessKeyId, request.S3Scope, ct)
            || await store.FindAccessKeyAsync(accessKeyId, ct) is not { } key)
            return AccessKeyErrors.NotFound(accessKeyId);
        await cache.RemoveAsync(CredentialCache.KeyFor(accessKeyId), ct); // takes effect on the next request
        return key.ToResponse();
    }

    private async ValueTask<Result<AccessKeyResponse>> SetDisabledAsync(
        string accessKeyId, DateTime? disabledAt, CancellationToken ct)
    {
        var changed = await store.SetAccessKeyDisabledAsync(accessKeyId, disabledAt, ct);
        await cache.RemoveAsync(CredentialCache.KeyFor(accessKeyId), ct); // takes effect on the next request
        if (disabledAt is not null)
            await store.DeleteSessionsOfAsync(accessKeyId, ct);
        if (!changed || await store.FindAccessKeyAsync(accessKeyId, ct) is not { } key)
            return AccessKeyErrors.NotFound(accessKeyId);
        return key.ToResponse();
    }
}
