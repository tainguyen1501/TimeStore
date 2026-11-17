using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Auth;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>Admin API sign-in end to end: sessions, CSRF, roles.</summary>
public sealed class AdminAuthTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutASession_TheAdminApiIs401_ButHealthIsOpen()
    {
        using var anonymous = server.AnonymousAdminClient();

        var buckets = await anonymous.GetAsync("/api/v1/buckets", _ct);
        var health = await anonymous.GetAsync("/health/live", _ct);

        Assert.Equal(HttpStatusCode.Unauthorized, buckets.StatusCode);
        Assert.Contains("AUTH_UNAUTHENTICATED", await buckets.Content.ReadAsStringAsync(_ct));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Login_SetsAStrictHttpOnlyCookie_AndMeTellsWhoAndWhat()
    {
        using var client = server.AnonymousAdminClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(TimeStoreServer.AccessKey, TimeStoreServer.SecretKey), Json, _ct);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ts_session=", StringComparison.Ordinal));
        var me = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me", Json, _ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", login.Headers.CacheControl?.ToString());
        Assert.Equal((TimeStoreServer.AccessKey, "admin"), (me!.AccessKeyId, me.Role));
        Assert.DoesNotContain(cookie.Split(';')[0]["ts_session=".Length..], await login.Content.ReadAsStringAsync(_ct)); // token only in the cookie
    }

    [Fact]
    public async Task Login_WithAWrongSecret_Is401()
    {
        using var client = server.AnonymousAdminClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TimeStoreServer.AccessKey, "nope"), Json, _ct);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("AUTH_INVALID_CREDENTIALS", await login.Content.ReadAsStringAsync(_ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://evil.example")]
    public async Task StateChangingRequests_FromAnotherOrigin_Are403(string? origin)
    {
        using var admin = await server.AdminClientAsync();
        admin.DefaultRequestHeaders.Remove("Origin");
        if (origin is not null)
            admin.DefaultRequestHeaders.Add("Origin", origin);

        var create = await admin.PostAsJsonAsync("/api/v1/buckets", new { name = $"csrf-{Guid.NewGuid():N}" }, Json, _ct);
        var read = await admin.GetAsync("/api/v1/buckets", _ct); // reads need no origin

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("AUTH_CROSS_SITE", await create.Content.ReadAsStringAsync(_ct));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task AViewer_MayReadButNotWrite()
    {
        using var admin = await server.AdminClientAsync();
        var viewer = (await (await admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("viewer", "viewer"), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        using var client = await server.AdminClientAsync(viewer.AccessKeyId, viewer.SecretKey);

        var read = await client.GetAsync("/api/v1/access-keys", _ct);
        var write = await client.PostAsJsonAsync("/api/v1/buckets", new { name = $"viewer-{Guid.NewGuid():N}" }, Json, _ct);
        var logout = await client.PostAsync("/api/v1/auth/logout", null, _ct); // signing out is always allowed

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Contains("AUTH_FORBIDDEN", await write.Content.ReadAsStringAsync(_ct));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task AKeyWithoutARole_CannotSignIn_UntilItGetsOne()
    {
        using var admin = await server.AdminClientAsync();
        var key = (await (await admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("app"), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        using var client = server.AnonymousAdminClient();

        var before = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(key.AccessKeyId, key.SecretKey), Json, _ct);
        var role = await admin.PostAsJsonAsync($"/api/v1/access-keys/{key.AccessKeyId}/role", new SetAccessKeyRoleRequest("admin"), Json, _ct);
        var after = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(key.AccessKeyId, key.SecretKey), Json, _ct);

        Assert.Equal("none", key.Role);
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        Assert.Equal("admin", (await role.Content.ReadFromJsonAsync<AccessKeyResponse>(Json, _ct))!.Role);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Logout_AndDisablingTheKey_EndTheSession()
    {
        using var admin = await server.AdminClientAsync();
        var key = (await (await admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("ops", "admin"), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        using var first = await server.AdminClientAsync(key.AccessKeyId, key.SecretKey);
        using var second = await server.AdminClientAsync(key.AccessKeyId, key.SecretKey);

        await first.PostAsync("/api/v1/auth/logout", null, _ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/auth/me", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/auth/me", _ct)).StatusCode);

        await admin.PostAsync($"/api/v1/access-keys/{key.AccessKeyId}/disable", null, _ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/auth/me", _ct)).StatusCode);
    }
}

/// <summary>Throttling has its own server: it blocks the test client's address for a while.</summary>
public sealed class AdminLoginThrottleTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>
{
    [Fact]
    public async Task RepeatedFailures_Are429_WithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = server.AnonymousAdminClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 7; i++)
            last = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TimeStoreServer.AccessKey, "wrong"), JsonSerializerOptions.Web, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Contains("AUTH_TOO_MANY_ATTEMPTS", await last.Content.ReadAsStringAsync(ct));
        Assert.True(last.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }
}
