using TimeStore.Application.Abstractions;

namespace TimeStore.Infra.Credentials;

/// <summary>Cache keys and lifetime of decrypted credentials, shared by the store that fills and the service that evicts.</summary>
internal static class CredentialCache
{
    private const string KeyPrefix = "credential:";

    /// <summary>Upper bound only: every change through the access key service evicts the entry at once.</summary>
    public static readonly CacheEntry Entry = new(TimeSpan.FromMinutes(5));

    public static string KeyFor(string accessKeyId) => KeyPrefix + accessKeyId;
}
