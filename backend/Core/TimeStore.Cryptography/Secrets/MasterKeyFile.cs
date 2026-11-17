using System.Security.Cryptography;

namespace TimeStore.Cryptography.Secrets;

/// <summary>
/// The master key file: 32 random bytes, created on first start with owner-only permissions (0600 on Unix). Losing it
/// makes every stored secret unreadable, so it is never overwritten.
/// </summary>
public static class MasterKeyFile
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Reads the key, or creates it when the file does not exist yet.</summary>
    public static byte[] LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return Load(path);

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory)
            Directory.CreateDirectory(directory);

        var key = RandomNumberGenerator.GetBytes(AesGcmSecretProtector.MasterKeyLength);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerReadWrite;
            using (var file = new FileStream(path, options))
            {
                file.Write(key);
                file.Flush(flushToDisk: true);
            }

            return key;
        }
        catch (IOException) when (File.Exists(path))
        {
            return Load(path); // another process created it first
        }
    }

    private static byte[] Load(string path)
    {
        var key = File.ReadAllBytes(path);
        return key.Length == AesGcmSecretProtector.MasterKeyLength
            ? key
            : throw new InvalidOperationException(
                $"The master key file '{path}' must contain exactly {AesGcmSecretProtector.MasterKeyLength} bytes.");
    }
}
