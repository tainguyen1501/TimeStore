namespace TimeStore.Contracts.AccessKeys;

/// <param name="Description">Optional label (who or what uses the key).</param>
/// <param name="Role">Admin API role: <c>none</c> (default, S3 only), <c>viewer</c>, <c>admin</c> or <c>backup</c>
/// (the backup API with signed requests only).</param>
/// <param name="S3Scope"><c>full</c> (default: S3 allowed unless a bucket policy denies) or <c>policy</c> (only what bucket policies allow).</param>
public sealed record CreateAccessKeyRequest(string? Description, string? Role = null, string? S3Scope = null);

/// <summary>A key without its secret: what list and get return.</summary>
public sealed record AccessKeyResponse(
    string AccessKeyId, string Description, string Role, DateTime CreatedAt, DateTime? DisabledAt, string S3Scope = "full");

/// <param name="S3Scope"><c>full</c> or <c>policy</c>.</param>
public sealed record SetAccessKeyScopeRequest(string S3Scope);

/// <param name="Role"><c>none</c>, <c>viewer</c>, <c>admin</c> or <c>backup</c>.</param>
public sealed record SetAccessKeyRoleRequest(string Role);

/// <summary>
/// Returned once, by create. The secret is not stored in plain text and cannot be read again; a lost secret means a
/// new key.
/// </summary>
public sealed record CreatedAccessKeyResponse(
    string AccessKeyId, string SecretKey, string Description, string Role, DateTime CreatedAt, string S3Scope = "full")
{
    /// <summary>Never print the secret (logs, debugger, exception messages).</summary>
    public override string ToString() => $"{nameof(CreatedAccessKeyResponse)} {{ {nameof(AccessKeyId)} = {AccessKeyId} }}";
}
