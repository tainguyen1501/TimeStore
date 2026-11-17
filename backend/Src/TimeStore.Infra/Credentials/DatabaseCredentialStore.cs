using System.Text;
using TimeStore.Application.Abstractions;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Credentials;

/// <summary>
/// The root key from configuration (break-glass: always valid, never stored) plus the active keys of the metadata
/// database, decrypted with <see cref="ISecretProtector"/> (associated data = access key id). Found keys are cached in
/// plain text in memory, so the hot path costs no query and no AES; misses are not cached, so random ids cannot fill
/// the cache.
/// </summary>
public sealed class DatabaseCredentialStore(
    RootCredential root, IMetadataStore store, ISecretProtector protector, ICacheService cache) : ICredentialStore
{
    public async ValueTask<AccessCredential?> FindAsync(string accessKeyId, CancellationToken ct)
    {
        if (string.Equals(accessKeyId, root.Credential.AccessKeyId, StringComparison.Ordinal))
            return root.Credential;

        var cacheKey = CredentialCache.KeyFor(accessKeyId);
        var credential = await cache.GetOrCreateAsync(cacheKey, token => LoadAsync(accessKeyId, token), CredentialCache.Entry, ct);
        if (credential is null)
            await cache.RemoveAsync(cacheKey, ct);
        return credential;
    }

    private async ValueTask<AccessCredential?> LoadAsync(string accessKeyId, CancellationToken ct)
    {
        if (await store.FindAccessKeyAsync(accessKeyId, ct) is not { IsActive: true } key)
            return null;

        // A row that does not decrypt (another master key, tampering) authenticates nobody.
        if (!protector.TryUnprotect(key.SecretCiphertext, Encoding.UTF8.GetBytes(accessKeyId), out var secret))
            return null;

        return new AccessCredential(accessKeyId, Encoding.UTF8.GetString(secret)) { S3Scope = key.S3Scope };
    }
}

/// <summary>The configured root key, as a distinct type so DI cannot confuse it with another credential.</summary>
public sealed record RootCredential(AccessCredential Credential);
