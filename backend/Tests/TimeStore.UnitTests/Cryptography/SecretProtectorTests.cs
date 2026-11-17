using System.Security.Cryptography;
using TimeStore.Cryptography.Secrets;

namespace TimeStore.UnitTests.Cryptography;

public sealed class SecretProtectorTests
{
    private static readonly byte[] Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"u8.ToArray();
    private static readonly byte[] Owner = "TSAAAAAAAAAAAAAAAAAA"u8.ToArray();
    private readonly AesGcmSecretProtector _protector = new(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.MasterKeyLength));

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips_AndHidesThePlaintext()
    {
        var protectedData = _protector.Protect(Secret, Owner);

        Assert.Equal(-1, protectedData.AsSpan().IndexOf(Secret));
        Assert.True(_protector.TryUnprotect(protectedData, Owner, out var plaintext));
        Assert.Equal(Secret, plaintext);
    }

    [Fact]
    public void Protect_UsesAFreshNonceEveryTime() =>
        Assert.NotEqual(_protector.Protect(Secret, Owner), _protector.Protect(Secret, Owner));

    [Fact]
    public void Unprotect_WithAnotherOwner_Fails() =>
        Assert.False(_protector.TryUnprotect(_protector.Protect(Secret, Owner), "TSBBBBBBBBBBBBBBBBBB"u8, out _));

    [Fact]
    public void Unprotect_WithAnotherMasterKey_Fails()
    {
        var other = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.MasterKeyLength));

        Assert.False(other.TryUnprotect(_protector.Protect(Secret, Owner), Owner, out _));
    }

    [Fact]
    public void Unprotect_AnyFlippedBit_Fails()
    {
        var protectedData = _protector.Protect(Secret, Owner);
        for (var i = 0; i < protectedData.Length; i++)
        {
            var tampered = protectedData.ToArray();
            tampered[i] ^= 0x01;
            Assert.False(_protector.TryUnprotect(tampered, Owner, out _), $"byte {i}");
        }
    }

    [Fact]
    public void Unprotect_TruncatedOrEmpty_Fails()
    {
        var protectedData = _protector.Protect(Secret, Owner);

        Assert.False(_protector.TryUnprotect(protectedData.AsSpan(0, 20), Owner, out _));
        Assert.False(_protector.TryUnprotect([], Owner, out _));
    }

    [Fact]
    public void Constructor_RejectsAShortMasterKey() =>
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16]));

    [Fact]
    public void MasterKeyFile_IsCreatedOnce_OwnerOnly_AndReloaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"), "master.key");
        try
        {
            var created = MasterKeyFile.LoadOrCreate(path);
            var loaded = MasterKeyFile.LoadOrCreate(path);

            Assert.Equal(AesGcmSecretProtector.MasterKeyLength, created.Length);
            Assert.Equal(created, loaded);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void MasterKeyFile_WithWrongLength_StopsStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timestore-{Guid.NewGuid():N}.key");
        File.WriteAllBytes(path, new byte[31]);
        try
        {
            Assert.Throws<InvalidOperationException>(() => MasterKeyFile.LoadOrCreate(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generator_ProducesAwsShapedKeys()
    {
        var id = AccessKeyGenerator.NewAccessKeyId();
        var secret = AccessKeyGenerator.NewSecretKey();

        Assert.Matches("^TS[A-Z2-7]{18}$", id);
        Assert.Matches("^[A-Za-z0-9]{40}$", secret);
        Assert.NotEqual(id, AccessKeyGenerator.NewAccessKeyId());
        Assert.NotEqual(secret, AccessKeyGenerator.NewSecretKey());
    }
}
