using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace TimeStore.Protocol.Routing;

/// <summary>
/// Percent-decoding of request paths and query strings into UTF-8 text. Strict: malformed escapes and byte sequences
/// that are not valid UTF-8 are rejected (the caller answers <c>InvalidURI</c>), never replaced.
/// </summary>
public static class S3PercentDecoder
{
    private const int StackLimit = 256;
    private const int MaxBytesPerChar = 3;

    /// <param name="plusAsSpace">True for query components (form encoding), false for paths ('+' is literal).</param>
    public static bool TryDecode(ReadOnlySpan<char> input, bool plusAsSpace, [NotNullWhen(true)] out string? value)
    {
        if (input.IndexOfAny('%', '+') < 0 || (!plusAsSpace && !input.Contains('%')))
        {
            value = input.ToString();
            return true;
        }

        var maxBytes = input.Length * MaxBytesPerChar;
        byte[]? rented = null;
        Span<byte> buffer = maxBytes <= StackLimit
            ? stackalloc byte[StackLimit]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            if (!TryDecodeToUtf8(input, plusAsSpace, buffer, out var length) || !Utf8.IsValid(buffer[..length]))
            {
                value = null;
                return false;
            }

            value = Encoding.UTF8.GetString(buffer[..length]);
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>UTF-8 byte length of the decoded form without allocating a string (key length limits).</summary>
    public static int Utf8Length(string decoded) => Encoding.UTF8.GetByteCount(decoded);

    private static bool TryDecodeToUtf8(ReadOnlySpan<char> input, bool plusAsSpace, Span<byte> output, out int length)
    {
        length = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '%')
            {
                if (i + 2 >= input.Length || !char.IsAsciiHexDigit(input[i + 1]) || !char.IsAsciiHexDigit(input[i + 2]))
                    return false;
                output[length++] = (byte)((HexValue(input[i + 1]) << 4) | HexValue(input[i + 2]));
                i += 2;
            }
            else if (c == '+' && plusAsSpace)
            {
                output[length++] = (byte)' ';
            }
            else if (char.IsAscii(c))
            {
                output[length++] = (byte)c;
            }
            else
            {
                // Raw non-ASCII (should be escaped, but some clients send it): keep it as UTF-8.
                var count = char.IsHighSurrogate(c) && i + 1 < input.Length ? 2 : 1;
                length += Encoding.UTF8.GetBytes(input.Slice(i, count), output[length..]);
                i += count - 1;
            }
        }

        return true;
    }

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
