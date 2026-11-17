using TimeStore.Contracts.Auth;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Auth;

/// <summary>A signed-in admin API caller: the access key and its role (root is always <c>admin</c>).</summary>
public sealed record AdminPrincipal(string AccessKeyId, string Role, DateTime ExpiresAt);

/// <param name="Token">The session token for the cookie. Shown once; only its SHA-256 is stored.</param>
public sealed record SignedIn(string Token, SessionResponse Session)
{
    /// <summary>Never print the token (logs, debugger, exception messages).</summary>
    public override string ToString() => $"{nameof(SignedIn)} {{ {nameof(Session)} = {Session} }}";
}

/// <param name="Lifetime">How long a session lasts from sign-in (absolute).</param>
public sealed record AdminSessionOptions(TimeSpan Lifetime)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);
}

/// <summary>
/// Admin API sign-in with an access key: access key id + secret key; only keys with role
/// viewer or admin (and the root key) may sign in. Sessions are server-side and revocable.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// A new session, or <c>AUTH_INVALID_CREDENTIALS</c> (the same answer for an unknown key, a wrong secret, a
    /// disabled key and a key without an admin role) or <c>AUTH_TOO_MANY_ATTEMPTS</c> after repeated failures.
    /// </summary>
    /// <param name="client">The caller's address, for throttling repeated failures.</param>
    ValueTask<Result<SignedIn>> LoginAsync(LoginRequest request, string client, CancellationToken ct);

    /// <summary>How long a throttled sign-in must wait (for <c>Retry-After</c>), or null when it may try now.</summary>
    TimeSpan? RetryAfter(string accessKeyId, string client);

    /// <summary>The principal of a session token, or null (unknown, expired, key disabled or demoted).</summary>
    ValueTask<AdminPrincipal?> AuthenticateAsync(string token, CancellationToken ct);

    /// <summary>Ends the session (no error when it is already gone).</summary>
    ValueTask LogoutAsync(string token, CancellationToken ct);

    /// <summary>Removes expired sessions (maintenance); returns how many.</summary>
    ValueTask<int> PurgeExpiredAsync(CancellationToken ct);
}
