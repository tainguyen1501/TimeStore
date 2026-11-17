using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.AccessKeys;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Cryptography.Secrets;
using TimeStore.Infra.Caching;
using TimeStore.Infra.Credentials;
using TimeStore.Infra.Services.AccessKeys;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.AccessKeys;

public sealed class AccessKeyServiceTests : IDisposable
{
    private static readonly AccessCredential Root = new("root-key", "root-secret-key");
    private readonly InMemoryMetadataStore _store = new();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly AccessKeyService _service;
    private readonly DatabaseCredentialStore _credentials;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public AccessKeyServiceTests()
    {
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.MasterKeyLength));
        var cache = new MemoryCacheService(_memory);
        _service = new AccessKeyService(_store, protector, cache, new FixedTimeProvider());
        _credentials = new DatabaseCredentialStore(new RootCredential(Root), _store, protector, cache);
    }

    public void Dispose() => _memory.Dispose();

    [Fact]
    public async Task Create_ReturnsTheSecretOnce_AndStoresOnlyCiphertext()
    {
        var created = (await _service.CreateAsync(new CreateAccessKeyRequest("ci"), _ct)).Value;

        var stored = (await _store.FindAccessKeyAsync(created.AccessKeyId, _ct))!;
        Assert.Equal(-1, stored.SecretCiphertext.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(created.SecretKey)));
        Assert.Equal("ci", stored.Description);
        Assert.DoesNotContain(created.SecretKey, created.ToString());
        Assert.Equal(new AccessKeyResponse(created.AccessKeyId, "ci", "none", created.CreatedAt, null), Assert.Single(await _service.ListAsync(_ct)));
    }

    [Fact]
    public async Task CredentialStore_FindsActiveKeys_NotDisabledOrDeletedOnes()
    {
        var created = (await _service.CreateAsync(new CreateAccessKeyRequest(null), _ct)).Value;
        Assert.Equal(created.SecretKey, (await _credentials.FindAsync(created.AccessKeyId, _ct))?.SecretKey); // now cached

        var disabled = await _service.DisableAsync(created.AccessKeyId, _ct);
        Assert.NotNull(disabled.Value.DisabledAt);
        Assert.Null(await _credentials.FindAsync(created.AccessKeyId, _ct)); // eviction took effect at once

        await _service.EnableAsync(created.AccessKeyId, _ct);
        Assert.NotNull(await _credentials.FindAsync(created.AccessKeyId, _ct));

        Assert.True((await _service.DeleteAsync(created.AccessKeyId, _ct)).IsSuccess);
        Assert.Null(await _credentials.FindAsync(created.AccessKeyId, _ct));
    }

    [Fact]
    public async Task CredentialStore_RootKey_IsAlwaysValid_AndNotManaged()
    {
        Assert.Same(Root, await _credentials.FindAsync(Root.AccessKeyId, _ct));
        Assert.Empty(await _service.ListAsync(_ct));
        Assert.Equal(AccessKeyErrors.Codes.NotFound, (await _service.DisableAsync(Root.AccessKeyId, _ct)).Error.Code);
        Assert.Equal(AccessKeyErrors.Codes.NotFound, (await _service.DeleteAsync(Root.AccessKeyId, _ct)).Error.Code);
    }

    [Fact]
    public async Task CredentialStore_UnknownKey_IsNull() =>
        Assert.Null(await _credentials.FindAsync("TSUNKNOWN0000000000X", _ct));

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData(null, 257)]
    public async Task Create_WithBadDescription_IsRejected(string? description, int length = 0)
    {
        var result = await _service.CreateAsync(new CreateAccessKeyRequest(description ?? new string('a', length)), _ct);

        Assert.Equal(AccessKeyErrors.Codes.InvalidDescription, result.Error.Code);
    }

    [Fact]
    public async Task UnknownKey_CannotBeDisabledEnabledOrDeleted()
    {
        Assert.Equal(AccessKeyErrors.Codes.NotFound, (await _service.DisableAsync("TSNOPE", _ct)).Error.Code);
        Assert.Equal(AccessKeyErrors.Codes.NotFound, (await _service.EnableAsync("TSNOPE", _ct)).Error.Code);
        Assert.Equal(AccessKeyErrors.Codes.NotFound, (await _service.DeleteAsync("TSNOPE", _ct)).Error.Code);
    }
}
