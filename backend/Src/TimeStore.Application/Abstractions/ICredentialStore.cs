namespace TimeStore.Application.Abstractions;

/// <summary>An S3 access key pair. The secret is needed in plain text: SigV4 is an HMAC over it.</summary>
public sealed record AccessCredential(string AccessKeyId, string SecretKey)
{
    /// <summary>S3 access without a bucket policy (<c>Domain.AccessKeys.AccessKeyScopes</c>).</summary>
    public string S3Scope { get; init; } = Domain.AccessKeys.AccessKeyScopes.Full;

    public const int AccessKeyMinLength = 3;
    public const int AccessKeyMaxLength = 128;
    public const int SecretKeyMinLength = 8;
    public const int SecretKeyMaxLength = 128;

    /// <summary>Never print the secret (logs, debugger, exception messages).</summary>
    public override string ToString() => $"{nameof(AccessCredential)} {{ {nameof(AccessKeyId)} = {AccessKeyId} }}";
}

/// <summary>
/// Where access keys come from: the root key from configuration and the keys stored encrypted in the metadata
/// database (<c>DatabaseCredentialStore</c>). Callers never know which.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The credential, or null when the access key id is unknown.</summary>
    ValueTask<AccessCredential?> FindAsync(string accessKeyId, CancellationToken ct);
}
