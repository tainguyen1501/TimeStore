using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Auth;
using TimeStore.Contracts.Auth;
using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Stores;
using TimeStore.Infra.Credentials;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Auth;

public sealed class AuthService(
    ICredentialStore credentials, IMetadataStore store, RootCredential root, LoginThrottle throttle,
    AdminSessionOptions options, TimeProvider clock) : IAuthService
{
    private const int TokenBytes = 32; // 256 bits from the CSPRNG (OWASP: at least 128)
    private const string KeyPrefix = "key:";
    private const string ClientPrefix = "client:";

    /// <summary>Compared against when the key is unknown, so every failure costs the same work.</summary>
    private static readonly byte[] DummySecretHash = SHA256.HashData(RandomNumberGenerator.GetBytes(TokenBytes));

    public async ValueTask<Result<SignedIn>> LoginAsync(LoginRequest request, string client, CancellationToken ct)
    {
        string[] keys = [KeyPrefix + request.AccessKeyId, ClientPrefix + client];
        if (RetryAfter(request.AccessKeyId, client) is { } wait)
            return AuthErrors.TooManyAttempts(wait);

        var credential = string.IsNullOrEmpty(request.AccessKeyId) ? null : await credentials.FindAsync(request.AccessKeyId, ct);
        var role = credential is null ? null : await RoleOfAsync(request.AccessKeyId, ct);

        // Fixed-length digests compared in constant time: no early exit, no length leak, same work for unknown keys.
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(request.SecretKey ?? string.Empty));
        var expected = credential is null ? DummySecretHash : SHA256.HashData(Encoding.UTF8.GetBytes(credential.SecretKey));
        var secretMatches = CryptographicOperations.FixedTimeEquals(given, expected);
        if (!secretMatches || credential is null || role is null || !AccessKeyRoles.CanSignIn(role))
        {
            throttle.Failed(keys);
            return AuthErrors.InvalidCredentials;
        }

        throttle.Succeeded(keys);
        var token = RandomNumberGenerator.GetBytes(TokenBytes);
        var now = clock.UtcNowMs;
        var session = new AdminSession(SHA256.HashData(token), credential.AccessKeyId, now, now + options.Lifetime);
        await store.CreateSessionAsync(session, ct);
        return new SignedIn(Base64Url.EncodeToString(token), new SessionResponse(session.AccessKeyId, role, session.ExpiresAt));
    }

    public TimeSpan? RetryAfter(string accessKeyId, string client) =>
        throttle.RetryAfter([KeyPrefix + accessKeyId, ClientPrefix + client]);

    public async ValueTask<AdminPrincipal?> AuthenticateAsync(string token, CancellationToken ct)
    {
        if (!TryHash(token, out var hash) || await store.FindSessionAsync(hash, ct) is not { } session)
            return null;

        if (session.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
        {
            await store.DeleteSessionAsync(hash, ct);
            return null;
        }

        // The key's current state decides: a disabled, deleted or demoted key loses its sessions at once.
        var role = await RoleOfAsync(session.AccessKeyId, ct);
        if (role is null || !AccessKeyRoles.CanSignIn(role))
        {
            await store.DeleteSessionsOfAsync(session.AccessKeyId, ct);
            return null;
        }

        return new AdminPrincipal(session.AccessKeyId, role, session.ExpiresAt);
    }

    public async ValueTask LogoutAsync(string token, CancellationToken ct)
    {
        if (TryHash(token, out var hash))
            await store.DeleteSessionAsync(hash, ct);
    }

    public async ValueTask<AdminPrincipal?> AuthenticateSignedAsync(string accessKeyId, CancellationToken ct) =>
        await RoleOfAsync(accessKeyId, ct) is { } role ? new AdminPrincipal(accessKeyId, role, clock.GetUtcNow().UtcDateTime) : null;

    public ValueTask<int> PurgeExpiredAsync(CancellationToken ct) =>
        store.DeleteExpiredSessionsAsync(clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>The admin role of an active key (root is always admin), or null when the key cannot be used.</summary>
    private async ValueTask<string?> RoleOfAsync(string accessKeyId, CancellationToken ct)
    {
        if (string.Equals(accessKeyId, root.Credential.AccessKeyId, StringComparison.Ordinal))
            return AccessKeyRoles.Admin;
        return await store.FindAccessKeyAsync(accessKeyId, ct) is { IsActive: true } key ? key.Role : null;
    }

    private static bool TryHash(string token, out byte[] hash)
    {
        hash = [];
        Span<byte> raw = stackalloc byte[TokenBytes];
        if (string.IsNullOrEmpty(token) || token.Length > 64
            || Base64Url.DecodeFromChars(token, raw, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != TokenBytes)
            return false;
        hash = SHA256.HashData(raw);
        return true;
    }
}
