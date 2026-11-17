using System.Security.Cryptography;

namespace TimeStore.Cryptography.Secrets;

/// <summary>New access key pairs in the shape S3 clients expect (AWS-like lengths and alphabets).</summary>
public static class AccessKeyGenerator
{
    public const string IdPrefix = "TS";
    public const int IdLength = 20;
    public const int SecretLength = 40;

    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string SecretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary><c>TS</c> + 18 upper-case base32 characters (≈ 90 bits).</summary>
    public static string NewAccessKeyId() =>
        IdPrefix + RandomNumberGenerator.GetString(Base32, IdLength - IdPrefix.Length);

    /// <summary>40 alphanumeric characters (≈ 238 bits); no '/' or '+', so it survives shells and URLs.</summary>
    public static string NewSecretKey() => RandomNumberGenerator.GetString(SecretAlphabet, SecretLength);
}
