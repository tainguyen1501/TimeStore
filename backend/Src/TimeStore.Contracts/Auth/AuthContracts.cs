namespace TimeStore.Contracts.Auth;

/// <summary>Sign-in with an access key: the access key id and its secret key.</summary>
public sealed record LoginRequest(string AccessKeyId, string SecretKey)
{
    /// <summary>Never print the secret (logs, debugger, exception messages).</summary>
    public override string ToString() => $"{nameof(LoginRequest)} {{ {nameof(AccessKeyId)} = {AccessKeyId} }}";
}

/// <summary>Who is signed in, with which role, until when.</summary>
public sealed record SessionResponse(string AccessKeyId, string Role, DateTime ExpiresAt);
