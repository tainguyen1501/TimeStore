using System.Buffers;
using System.Text;
using TimeStore.Cryptography;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;

namespace TimeStore.Protocol.Streaming;

/// <param name="Mode">One of the three streaming payload modes.</param>
/// <param name="DecodedLength">Value of <c>x-amz-decoded-content-length</c>: the exact payload size.</param>
/// <param name="TrailerChecksum">Checksum announced in <c>x-amz-trailer</c> (trailer modes only).</param>
/// <param name="Principal">The authenticated request: seed signature and signing key for chunk signatures.</param>
public sealed record AwsChunkedSettings(
    PayloadMode Mode, long DecodedLength, ChecksumAlgorithm? TrailerChecksum, SigV4Principal Principal);

/// <summary>
/// Decodes an <c>aws-chunked</c> request body while verifying it. Yields only the payload bytes.
/// <list type="bullet">
/// <item>Signed modes: every chunk signature, chained from the request signature (constant-time compare).</item>
/// <item>Trailer modes: the checksum trailer against the payload; in the signed-trailer mode also its signature.</item>
/// <item>Always: framing (hex sizes, CRLF, 4 KiB line limit) and the total against <c>x-amz-decoded-content-length</c>.</item>
/// </list>
/// Failures throw <see cref="S3BodyVerificationException"/>. Lenient where clients differ: a missing CRLF after the
/// final chunk, and chunk signatures sent in unsigned mode (ignored).
/// </summary>
public sealed class AwsChunkedStream : Stream
{
    private const int BufferSize = 64 * 1024;
    private const int MaxLineLength = 4096;
    private const int MaxHexDigits = 16;
    private const int MaxTrailerLines = 8; // a checksum and the trailer signature; bounds work per request
    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';
    private const string ChunkSignatureExtension = ";chunk-signature=";

    private readonly Stream _inner;
    private readonly AwsChunkedSettings _settings;
    private readonly bool _signed;
    private readonly IncrementalSha256? _chunkHash;
    private readonly IChecksum? _checksum;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _start;
    private int _end;
    private bool _innerEnded;
    private State _state = State.ChunkHeader;
    private long _chunkRemaining;
    private long _decoded;
    private string? _chunkSignature;
    private string _previousSignature;

    public AwsChunkedStream(Stream inner, AwsChunkedSettings settings, IChecksumFactory checksums)
    {
        _inner = inner;
        _settings = settings;
        _signed = settings.Mode is PayloadMode.StreamingSigned or PayloadMode.StreamingSignedTrailer;
        _chunkHash = _signed ? Signing.CreateSha256() : null;
        _checksum = settings.TrailerChecksum is { } algorithm ? checksums.Create(algorithm) : null;
        _previousSignature = settings.Principal.Signature;
    }

    private enum State
    {
        ChunkHeader,
        ChunkData,
        ChunkEnd,
        Trailer,
        Done,
    }

    private bool HasTrailer => _settings.Mode is PayloadMode.StreamingSignedTrailer or PayloadMode.StreamingUnsignedTrailer;

    /// <summary>The trailer checksum, once verified against the payload (null before the end or without a trailer).</summary>
    public VerifiedChecksum? VerifiedChecksum { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (true)
        {
            switch (_state)
            {
                case State.Done:
                    return 0;

                case State.ChunkHeader:
                    await ReadChunkHeaderAsync(ct);
                    continue;

                case State.ChunkData when _chunkRemaining == 0:
                    _state = State.ChunkEnd;
                    continue;

                case State.ChunkData:
                {
                    if (destination.IsEmpty)
                        return 0;
                    var slice = destination[..(int)Math.Min(destination.Length, _chunkRemaining)];
                    var read = await ReadPayloadAsync(slice, ct);
                    var data = slice.Span[..read];
                    _chunkHash?.Append(data);
                    _checksum?.Append(data);
                    _chunkRemaining -= read;
                    _decoded += read;

                    // Last payload byte: finish every check now (chunk and final signatures, trailer, length), so a
                    // reader that stops after exactly x-amz-decoded-content-length bytes still gets the failure.
                    if (_chunkRemaining == 0 && _decoded == _settings.DecodedLength)
                        await CompleteAsync(ct);
                    return read;
                }

                case State.ChunkEnd:
                    await ExpectCrlfAsync(ct);
                    VerifyChunkSignature();
                    _state = State.ChunkHeader;
                    continue;

                case State.Trailer:
                    await ReadTrailerAsync(ct);
                    Finish();
                    continue;
            }
        }
    }

    /// <summary>Runs the remaining framing and checks after the last payload byte, up to <see cref="State.Done"/>.</summary>
    private async ValueTask CompleteAsync(CancellationToken ct)
    {
        while (_state != State.Done)
        {
            switch (_state)
            {
                case State.ChunkData:
                    _state = State.ChunkEnd; // _chunkRemaining is 0 here
                    break;
                case State.ChunkEnd:
                    await ExpectCrlfAsync(ct);
                    VerifyChunkSignature();
                    _state = State.ChunkHeader;
                    break;
                case State.ChunkHeader:
                    await ReadChunkHeaderAsync(ct); // must be the 0-byte chunk: any size exceeds the declared length
                    break;
                case State.Trailer:
                    await ReadTrailerAsync(ct);
                    Finish();
                    break;
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>Synchronous reads are not supported (Kestrel disallows synchronous I/O; no sync-over-async).</summary>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
            _chunkHash?.Dispose();
            _checksum?.Dispose();
        }

        base.Dispose(disposing);
    }

    private async ValueTask ReadChunkHeaderAsync(CancellationToken ct)
    {
        if (await ReadLineAsync(ct) is not var (start, length))
            throw Fail(S3BodyErrors.Incomplete);

        var line = _buffer.AsSpan(start, length);
        var semicolon = line.IndexOf((byte)';');
        var size = ParseHex(semicolon < 0 ? line : line[..semicolon]);

        _chunkSignature = null;
        if (semicolon >= 0 && Encoding.ASCII.GetString(line[semicolon..]) is var extension
            && extension.StartsWith(ChunkSignatureExtension, StringComparison.Ordinal))
            _chunkSignature = extension[ChunkSignatureExtension.Length..];
        if (_signed && _chunkSignature is null)
            throw Fail(S3BodyErrors.Malformed("a chunk is missing its chunk-signature."));
        if (size > (ulong)(_settings.DecodedLength - _decoded))
            throw Fail(S3BodyErrors.Malformed("the chunks are larger than x-amz-decoded-content-length."));

        if (size > 0)
        {
            _chunkRemaining = (long)size;
            _state = State.ChunkData;
            return;
        }

        // Final chunk (0 bytes): its signature covers the empty payload.
        VerifyChunkSignature();
        if (HasTrailer)
        {
            _state = State.Trailer;
            return;
        }

        // "0;chunk-signature=...\r\n\r\n": the closing empty line is optional for robustness.
        if (await ReadLineAsync(ct) is var (_, closing) && closing != 0)
            throw Fail(S3BodyErrors.Malformed("unexpected data after the final chunk."));
        Finish();
    }

    private void VerifyChunkSignature()
    {
        if (!_signed)
            return;

        Span<byte> hash = stackalloc byte[Signing.Sha256Length];
        _chunkHash!.GetHashAndReset(hash);
        var principal = _settings.Principal;
        var stringToSign = $"{SigV4Names.ChunkAlgorithm}\n{principal.Timestamp}\n{principal.Scope}\n{_previousSignature}\n"
            + $"{SigV4Names.EmptySha256}\n{Convert.ToHexStringLower(hash)}";

        Span<byte> expected = stackalloc byte[Signing.Sha256Length];
        SigV4Signer.Sign(principal.SigningKey, stringToSign, expected);
        if (!SigV4Signer.Matches(_chunkSignature!, expected))
            throw Fail(S3BodyErrors.ChunkSignatureMismatch);
        _previousSignature = Convert.ToHexStringLower(expected);
    }

    private async ValueTask ReadTrailerAsync(CancellationToken ct)
    {
        string? checksumName = null, checksumValue = null, trailerSignature = null;
        var lines = 0;
        while (await ReadLineAsync(ct) is var (start, length) && length > 0)
        {
            if (++lines > MaxTrailerLines)
                throw Fail(S3BodyErrors.InvalidTrailer($"more than {MaxTrailerLines} trailer lines."));
            var line = Encoding.ASCII.GetString(_buffer.AsSpan(start, length));
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw Fail(S3BodyErrors.InvalidTrailer("a trailer line is not 'name:value'."));
            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (name == S3BodyNames.TrailerSignature)
                trailerSignature = value;
            else if (name.StartsWith(S3BodyNames.ChecksumHeaderPrefix, StringComparison.Ordinal))
                (checksumName, checksumValue) = (name, value);
        }

        var algorithm = _settings.TrailerChecksum!.Value;
        if (checksumName != S3Checksums.HeaderName(algorithm) || checksumValue is null)
            throw Fail(S3BodyErrors.InvalidTrailer($"expected {S3Checksums.HeaderName(algorithm)} as announced in x-amz-trailer."));
        if (!S3Checksums.TryDecode(checksumValue, algorithm, out var claimed))
            throw Fail(S3BodyErrors.InvalidChecksum(checksumName));

        Span<byte> actual = stackalloc byte[_checksum!.Length];
        _checksum.GetHashAndReset(actual);
        if (!actual.SequenceEqual(claimed))
            throw Fail(S3BodyErrors.BadDigest);

        if (_settings.Mode == PayloadMode.StreamingSignedTrailer)
            VerifyTrailerSignature($"{checksumName}:{checksumValue}\n", trailerSignature);
        VerifiedChecksum = new VerifiedChecksum(algorithm, claimed);
    }

    /// <summary>Trailer string to sign: algorithm, timestamp, scope, previous signature, SHA-256 of the trailer lines.</summary>
    private void VerifyTrailerSignature(string canonicalTrailer, string? trailerSignature)
    {
        if (trailerSignature is null)
            throw Fail(S3BodyErrors.InvalidTrailer($"{S3BodyNames.TrailerSignature} is required in this mode."));

        var principal = _settings.Principal;
        var stringToSign = $"{SigV4Names.TrailerAlgorithm}\n{principal.Timestamp}\n{principal.Scope}\n{_previousSignature}\n"
            + SigV4Signer.Sha256Hex(canonicalTrailer);
        Span<byte> expected = stackalloc byte[Signing.Sha256Length];
        SigV4Signer.Sign(principal.SigningKey, stringToSign, expected);
        if (!SigV4Signer.Matches(trailerSignature, expected))
            throw Fail(S3BodyErrors.TrailerSignatureMismatch);
    }

    private void Finish()
    {
        if (_decoded != _settings.DecodedLength)
            throw Fail(S3BodyErrors.Incomplete);
        _state = State.Done;
    }

    /// <summary>Payload bytes: from the buffer first, then straight from the inner stream (no extra copy).</summary>
    private async ValueTask<int> ReadPayloadAsync(Memory<byte> destination, CancellationToken ct)
    {
        int read;
        if (_end > _start)
        {
            read = Math.Min(destination.Length, _end - _start);
            _buffer.AsSpan(_start, read).CopyTo(destination.Span);
            _start += read;
            return read;
        }

        read = _innerEnded ? 0 : await _inner.ReadAsync(destination, ct);
        if (read == 0)
            throw Fail(S3BodyErrors.Incomplete);
        return read;
    }

    private async ValueTask ExpectCrlfAsync(CancellationToken ct)
    {
        if (await ReadLineAsync(ct) is not var (_, length))
            throw Fail(S3BodyErrors.Incomplete);
        if (length != 0)
            throw Fail(S3BodyErrors.Malformed("a chunk is longer than its declared size."));
    }

    /// <summary>
    /// The next line (without CR LF) as a slice of the buffer, valid until the next read; null at the end of the body.
    /// </summary>
    private async ValueTask<(int Start, int Length)?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf(Lf);
            if (newline >= 0)
            {
                var start = _start;
                _start += newline + 1;
                var length = newline > 0 && available[newline - 1] == Cr ? newline - 1 : newline;
                return length > MaxLineLength
                    ? throw Fail(S3BodyErrors.Malformed("a chunk header or trailer line is too long."))
                    : (start, length);
            }

            if (available.Length > MaxLineLength)
                throw Fail(S3BodyErrors.Malformed("a chunk header or trailer line is too long."));

            if (_innerEnded)
            {
                if (available.IsEmpty)
                    return null;
                var start = _start;
                _start = _end;
                var length = available[^1] == Cr ? available.Length - 1 : available.Length;
                return (start, length);
            }

            await FillAsync(ct);
        }
    }

    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }

        var read = await _inner.ReadAsync(_buffer.AsMemory(_end), ct);
        if (read == 0)
            _innerEnded = true;
        _end += read;
    }

    private static ulong ParseHex(ReadOnlySpan<byte> digits)
    {
        if (digits.IsEmpty || digits.Length > MaxHexDigits)
            throw Fail(S3BodyErrors.Malformed("a chunk size is not a hexadecimal number."));

        ulong value = 0;
        foreach (var digit in digits)
        {
            var c = (char)digit;
            if (!char.IsAsciiHexDigit(c))
                throw Fail(S3BodyErrors.Malformed("a chunk size is not a hexadecimal number."));
            value = (value << 4) | (uint)(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
        }

        return value;
    }

    private static S3BodyVerificationException Fail(Primitives.AppError error) => new(error);
}
