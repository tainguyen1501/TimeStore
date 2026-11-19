namespace TimeStore.Domain.AccessKeys;

/// <summary>
/// An S3 access key stored in the metadata database. The secret is kept encrypted (<see cref="SecretCiphertext"/>,
/// ISecretProtector with the access key id as associated data); SigV4 needs it in plain text only in memory.
/// </summary>
/// <param name="Role">What the key may do on the admin API (<see cref="AccessKeyRoles"/>); S3 is open to every active key.</param>
/// <param name="DisabledAt">When the key was disabled; null while it is active.</param>
public sealed record AccessKey(
    string AccessKeyId,
    byte[] SecretCiphertext,
    string Description,
    string Role,
    DateTime CreatedAt,
    DateTime? DisabledAt)
{
    public const int DescriptionMaxLength = 256;

    public bool IsActive => DisabledAt is null;

    /// <summary>What the key may do on S3 by itself (<see cref="AccessKeyScopes"/>); bucket policies apply on top.</summary>
    public string S3Scope { get; init; } = AccessKeyScopes.Full;
}

/// <summary>
/// S3 access of an access key without a bucket policy (docs/research/s3-bucket-policy.md). The root key ignores
/// bucket policies altogether.
/// </summary>
public static class AccessKeyScopes
{
    /// <summary>Everything, unless a bucket policy denies it (an identity of the bucket owner). The default.</summary>
    public const string Full = "full";

    /// <summary>Nothing, unless a bucket policy allows it (like a principal from another account).</summary>
    public const string Policy = "policy";

    public static bool IsValid(string? scope) => scope is Full or Policy;
}

/// <summary>
/// Admin API roles of an access key (sign-in with access key id + secret key). The root key from
/// configuration is always <see cref="Admin"/>.
/// </summary>
public static class AccessKeyRoles
{
    /// <summary>S3 only: cannot sign in to the admin API. The default for new keys.</summary>
    public const string None = "none";

    /// <summary>Admin API, read only.</summary>
    public const string Viewer = "viewer";

    /// <summary>Admin API, everything.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// The backup API only (<c>/api/v1/backups</c>, requests signed with SigV4), for the programs that run backups;
    /// never the admin UI, never archives with the master key.
    /// </summary>
    public const string Backup = "backup";

    public static bool IsValid(string? role) => role is None or Viewer or Admin or Backup;

    public static bool CanSignIn(string role) => role is Viewer or Admin;

    /// <summary>Who may call the backup API.</summary>
    public static bool CanBackUp(string role) => role is Admin or Backup;
}

/// <summary>A signed-in admin API session. Only the SHA-256 of the session token is stored.</summary>
public sealed record AdminSession(byte[] TokenHash, string AccessKeyId, DateTime CreatedAt, DateTime ExpiresAt);
