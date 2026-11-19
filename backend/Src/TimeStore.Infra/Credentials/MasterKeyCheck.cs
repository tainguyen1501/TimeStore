using System.Text;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Credentials;

/// <summary>
/// Whether the configured master key is the one the stored access keys were encrypted with. Checked at startup: a
/// wrong key (a new one, the key of another server) would otherwise only show as access keys that stop signing.
/// </summary>
public static class MasterKeyCheck
{
    /// <returns>How many stored access keys the key does not decrypt, out of how many.</returns>
    public static async ValueTask<(int Unreadable, int Total)> CountUnreadableAsync(
        IMetadataStore store, ISecretProtector protector, CancellationToken ct)
    {
        var keys = await store.ListAccessKeysAsync(ct);
        var unreadable = 0;
        foreach (var key in keys)
        {
            if (protector.TryUnprotect(key.SecretCiphertext, Encoding.UTF8.GetBytes(key.AccessKeyId), out var secret))
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            else
                unreadable++;
        }

        return (unreadable, keys.Count);
    }
}
