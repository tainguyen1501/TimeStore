using System.Security.Cryptography;
using System.Text;

namespace TimeStore.Cryptography.Secrets;

/// <summary>
/// Reads the master key the operator provides: a value (<see cref="Parse"/>) or a file (<see cref="Load"/>). TimeStore
/// never creates one: a missing key stops the server, since a new key would silently break every stored access key.
/// Create one with <c>openssl rand -base64 32</c>. Messages never quote the key.
/// </summary>
public static class MasterKeyFile
{
    /// <summary>The key written as Base64 (44 characters): 32 bytes once decoded.</summary>
    public static byte[] Parse(string text, string source)
    {
        var buffer = new byte[AesGcmSecretProtector.MasterKeyLength + 3]; // room to notice a longer key
        try
        {
            if (!Convert.TryFromBase64String(text.Trim(), buffer, out var written)
                || written != AesGcmSecretProtector.MasterKeyLength)
            {
                throw new InvalidOperationException(
                    $"{source} must be {AesGcmSecretProtector.MasterKeyLength} bytes written as Base64 "
                    + "(create one with: openssl rand -base64 32).");
            }

            return buffer[..AesGcmSecretProtector.MasterKeyLength];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// A file holding the key as Base64 text (<c>openssl rand -base64 32</c>) or as the 32 raw bytes (a
    /// <c>master.key</c> that earlier versions created inside the data directory).
    /// </summary>
    public static byte[] Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"The master key file '{path}' does not exist.");

        var content = File.ReadAllBytes(path);
        try
        {
            return content.Length == AesGcmSecretProtector.MasterKeyLength
                ? content.ToArray()
                : Parse(Encoding.ASCII.GetString(content), $"The master key file '{path}'");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }
}
