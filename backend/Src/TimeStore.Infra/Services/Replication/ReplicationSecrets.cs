using System.Text;
using TimeStore.Application.Abstractions;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Replication;

namespace TimeStore.Infra.Services.Replication;

/// <summary>
/// The destination secret at rest: <see cref="ISecretProtector"/> with the source bucket's id as associated data (one
/// rule per bucket, and the id never changes when the rule is edited), so a ciphertext copied to another rule does not
/// decrypt.
/// </summary>
internal static class ReplicationSecrets
{
    public static byte[] Protect(ISecretProtector protector, Guid bucketId, string secret) =>
        protector.Protect(Encoding.UTF8.GetBytes(secret), bucketId.ToByteArray(bigEndian: true));

    /// <summary>The destination with its secret, or null when the secret does not decrypt (another master key).</summary>
    public static ReplicationDestination? Destination(ISecretProtector protector, ReplicationRule rule)
    {
        if (!protector.TryUnprotect(rule.SecretCiphertext, rule.BucketId.ToByteArray(bigEndian: true), out var plaintext))
            return null;
        try
        {
            return new ReplicationDestination(
                new Uri(rule.Endpoint, UriKind.Absolute), rule.TargetBucket, rule.Region, rule.AccessKeyId, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }
}
