using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Auth;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Auth;
using TimeStore.Cryptography.Secrets;
using TimeStore.Infra.Caching;
using TimeStore.Infra.Credentials;
using TimeStore.Infra.Services.AccessKeys;
using TimeStore.Infra.Services.Auth;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Auth;

public sealed class AuthServiceTests : IDisposable
{
    private static readonly AccessCredential Root = new("root-key", "root-secret-key");
    private const string Client = "203.0.113.7";

    private readonly InMemoryMetadataStore _store = new();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly ManualTimeProvider _clock = new();
    private readonly AccessKeyService _keys;
    private readonly AuthService _auth;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public AuthServiceTests()
    {
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.MasterKeyLength));
        var cache = new MemoryCacheService(_memory);
        var rootCredential = new RootCredential(Root);
        _keys = new AccessKeyService(_store, protector, cache, _clock);
        _auth = new AuthService(
            new DatabaseCredentialStore(rootCredential, _store, protector, cache), _store, rootCredential,
            new LoginThrottle(_clock), new AdminSessionOptions(TimeSpan.FromHours(24)), _clock);
    }

    public void Dispose() => _memory.Dispose();

    private ValueTask<TimeStore.Primitives.Result<SignedIn>> Login(string key, string secret, string client = Client) =>
        _auth.LoginAsync(new LoginRequest(key, secret), client, _ct);

    private async Task<CreatedAccessKeyResponse> KeyAsync(string role) =>
        (await _keys.CreateAsync(new CreateAccessKeyRequest("test", role), _ct)).Value;

    [Fact]
    public async Task Root_SignsInAsAdmin_AndTheTokenAuthenticates()
    {
        var signedIn = (await Login(Root.AccessKeyId, Root.SecretKey)).Value;
        var principal = await _auth.AuthenticateAsync(signedIn.Token, _ct);

        Assert.Equal(("root-key", "admin"), (signedIn.Session.AccessKeyId, signedIn.Session.Role));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddHours(24).Ticks / TimeSpan.TicksPerMillisecond, signedIn.Session.ExpiresAt.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal(("root-key", "admin"), (principal!.AccessKeyId, principal.Role));
        Assert.DoesNotContain(signedIn.Token, signedIn.ToString());
    }

    [Fact]
    public async Task OnlyTheTokenHash_IsStored()
    {
        var signedIn = (await Login(Root.AccessKeyId, Root.SecretKey)).Value;
        var raw = System.Buffers.Text.Base64Url.DecodeFromChars(signedIn.Token);

        Assert.Null(await _store.FindSessionAsync(raw, _ct));
        Assert.NotNull(await _store.FindSessionAsync(SHA256.HashData(raw), _ct));
    }

    [Fact]
    public async Task KeysWithAnAdminRole_SignIn_KeysWithout_DoNot()
    {
        var viewer = await KeyAsync("viewer");
        var none = await KeyAsync("none");

        Assert.Equal("viewer", (await Login(viewer.AccessKeyId, viewer.SecretKey)).Value.Session.Role);
        Assert.Equal(AuthErrors.Codes.InvalidCredentials, (await Login(none.AccessKeyId, none.SecretKey)).Error.Code);
    }

    [Fact]
    public async Task EveryFailure_HasTheSameAnswer()
    {
        var none = await KeyAsync("none");
        var admin = await KeyAsync("admin");
        await _keys.DisableAsync(admin.AccessKeyId, _ct);

        var answers = new[]
        {
            await Login("TSUNKNOWN00000000000", "whatever"), await Login(Root.AccessKeyId, "wrong"),
            await Login(none.AccessKeyId, none.SecretKey), await Login(admin.AccessKeyId, admin.SecretKey),
            await Login("", ""),
        };

        Assert.All(answers, a => Assert.Equal((AuthErrors.Codes.InvalidCredentials, AuthErrors.InvalidCredentials.Message), (a.Error.Code, a.Error.Message)));
    }

    [Fact]
    public async Task ASession_EndsWithItsKey_DisabledDemotedOrDeleted()
    {
        var disabled = await KeyAsync("admin");
        var demoted = await KeyAsync("admin");
        var deleted = await KeyAsync("viewer");
        var tokens = new List<string>();
        foreach (var key in new[] { disabled, demoted, deleted })
            tokens.Add((await Login(key.AccessKeyId, key.SecretKey)).Value.Token);

        await _keys.DisableAsync(disabled.AccessKeyId, _ct);
        await _keys.SetRoleAsync(demoted.AccessKeyId, new SetAccessKeyRoleRequest("none"), _ct);
        await _keys.DeleteAsync(deleted.AccessKeyId, _ct);

        foreach (var token in tokens)
            Assert.Null(await _auth.AuthenticateAsync(token, _ct));
    }

    [Fact]
    public async Task ASessionExpires_AfterItsLifetime_AndIsPurged()
    {
        var token = (await Login(Root.AccessKeyId, Root.SecretKey)).Value.Token;
        var other = (await Login(Root.AccessKeyId, Root.SecretKey)).Value.Token;

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        Assert.Null(await _auth.AuthenticateAsync(token, _ct));
        Assert.Equal(1, await _auth.PurgeExpiredAsync(_ct)); // the other one, never presented again
        Assert.Null(await _auth.AuthenticateAsync(other, _ct));
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var token = (await Login(Root.AccessKeyId, Root.SecretKey)).Value.Token;

        await _auth.LogoutAsync(token, _ct);

        Assert.Null(await _auth.AuthenticateAsync(token, _ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MalformedTokens_AuthenticateNobody(string token) => Assert.Null(await _auth.AuthenticateAsync(token, _ct));

    [Fact]
    public async Task RepeatedFailures_AreThrottled_EvenForTheRightSecret_ThenRelease()
    {
        for (var i = 0; i < LoginThrottle.FreeFailures; i++)
            Assert.Equal(AuthErrors.Codes.InvalidCredentials, (await Login(Root.AccessKeyId, "wrong")).Error.Code);
        Assert.Equal(AuthErrors.Codes.InvalidCredentials, (await Login(Root.AccessKeyId, "wrong")).Error.Code); // 6th: now blocked

        var blocked = await Login(Root.AccessKeyId, Root.SecretKey);
        Assert.Equal(AuthErrors.Codes.TooManyAttempts, blocked.Error.Code);
        Assert.NotNull(_auth.RetryAfter(Root.AccessKeyId, Client));

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await Login(Root.AccessKeyId, Root.SecretKey)).IsSuccess);
        Assert.Null(_auth.RetryAfter(Root.AccessKeyId, Client)); // a success clears the counters
    }

    [Fact]
    public async Task Throttling_CountsTheClientAcrossKeys()
    {
        for (var i = 0; i <= LoginThrottle.FreeFailures; i++)
            await Login($"TSGUESS{i:D13}", "wrong");

        Assert.Equal(AuthErrors.Codes.TooManyAttempts, (await Login(Root.AccessKeyId, Root.SecretKey)).Error.Code);
        Assert.True((await Login(Root.AccessKeyId, Root.SecretKey, client: "198.51.100.1")).IsSuccess); // another client
    }
}
