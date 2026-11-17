using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace TimeStore.Cryptography.Secrets;

/// <summary>
/// Encrypts secrets at rest (access-key secrets). The only type that encrypts or decrypts secrets; callers never touch
/// AES directly. <paramref name="associatedData"/> binds a ciphertext to its owner (e.g. the access key id), so a
/// ciphertext copied to another row does not decrypt.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);

    /// <summary>False for tampered data, another owner, another master key or an unknown format.</summary>
    bool TryUnprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> associatedData, [NotNullWhen(true)] out byte[]? plaintext);
}

/// <summary>
/// AES-256-GCM. Layout: <c>version (1) | key id (4) | nonce (12) | tag (16) | ciphertext</c>. The key id (first bytes of
/// SHA-256 of the master key) lets a future rotation keep old keys around and pick the right one; the version byte
/// lets the format change.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    public const int MasterKeyLength = 32;
    private const byte Version = 1;
    private const int KeyIdLength = 4;
    private const int NonceLength = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagLength = 16; // AesGcm.TagByteSizes.MaxSize
    private const int HeaderLength = 1 + KeyIdLength + NonceLength + TagLength;

    private readonly byte[] _masterKey;
    private readonly uint _keyId;

    public AesGcmSecretProtector(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != MasterKeyLength)
            throw new ArgumentException($"The master key must be {MasterKeyLength} bytes.", nameof(masterKey));
        _masterKey = masterKey.ToArray();
        Span<byte> hash = stackalloc byte[Signing.Sha256Length];
        SHA256.HashData(masterKey, hash);
        _keyId = BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[HeaderLength + plaintext.Length];
        output[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1, KeyIdLength), _keyId);
        var nonce = output.AsSpan(1 + KeyIdLength, NonceLength);
        var tag = output.AsSpan(1 + KeyIdLength + NonceLength, TagLength);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_masterKey, TagLength);
        aes.Encrypt(nonce, plaintext, output.AsSpan(HeaderLength), tag, associatedData);
        return output;
    }

    public bool TryUnprotect(
        ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> associatedData, [NotNullWhen(true)] out byte[]? plaintext)
    {
        plaintext = null;
        if (protectedData.Length < HeaderLength || protectedData[0] != Version
            || BinaryPrimitives.ReadUInt32BigEndian(protectedData.Slice(1, KeyIdLength)) != _keyId)
            return false;

        var result = new byte[protectedData.Length - HeaderLength];
        try
        {
            using var aes = new AesGcm(_masterKey, TagLength);
            aes.Decrypt(
                protectedData.Slice(1 + KeyIdLength, NonceLength), protectedData[HeaderLength..],
                protectedData.Slice(1 + KeyIdLength + NonceLength, TagLength), result, associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(result);
            return false;
        }

        plaintext = result;
        return true;
    }
}
