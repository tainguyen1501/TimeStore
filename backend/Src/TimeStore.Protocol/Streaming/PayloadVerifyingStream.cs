using TimeStore.Cryptography;
using TimeStore.Cryptography.Checksums;

namespace TimeStore.Protocol.Streaming;

/// <summary>
/// Passes a plain (not aws-chunked) body through while hashing it; at the end compares with what the client declared:
/// the hex SHA-256 of <c>x-amz-content-sha256</c> and/or an <c>x-amz-checksum-*</c> header. Throws
/// <see cref="S3BodyVerificationException"/> on mismatch. With a known length the check runs in the read that returns
/// the last byte, so a reader that stops after exactly that many bytes still sees the failure.
/// </summary>
public sealed class PayloadVerifyingStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[]? _expectedSha256;
    private readonly IncrementalSha256? _sha256;
    private readonly IChecksum? _checksum;
    private readonly byte[]? _expectedChecksum;
    private readonly long? _length;
    private long _read;
    private bool _verified;

    /// <param name="expectedSha256Hex">Hex SHA-256 to enforce, or null.</param>
    /// <param name="checksum">Checksum to compute, or null.</param>
    /// <param name="expectedChecksum">Its declared digest (from the header), required with <paramref name="checksum"/>.</param>
    /// <param name="length">Payload length when known (Content-Length or decoded length); null: verify at end of stream.</param>
    public PayloadVerifyingStream(
        Stream inner, string? expectedSha256Hex, IChecksum? checksum, byte[]? expectedChecksum, long? length)
    {
        _inner = inner;
        _length = length;
        if (expectedSha256Hex is not null)
        {
            _expectedSha256 = Convert.FromHexString(expectedSha256Hex);
            _sha256 = Signing.CreateSha256();
        }

        _checksum = checksum;
        _expectedChecksum = expectedChecksum;
    }

    /// <summary>The declared checksum, once verified against the payload (null before the end or without one).</summary>
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
        var read = await _inner.ReadAsync(destination, ct);
        if (read > 0)
        {
            var data = destination.Span[..read];
            _sha256?.Append(data);
            _checksum?.Append(data);
            _read += read;
            if (_read == _length)
                Verify();
            else if (_read > _length)
                throw new S3BodyVerificationException(S3BodyErrors.Incomplete);
        }
        else if (!destination.IsEmpty)
        {
            if (_length is { } length && _read != length)
                throw new S3BodyVerificationException(S3BodyErrors.Incomplete);
            Verify();
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

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
        if (disposing)
        {
            _sha256?.Dispose();
            _checksum?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Verify()
    {
        if (_verified)
            return;
        _verified = true;

        if (_sha256 is not null)
        {
            Span<byte> hash = stackalloc byte[Signing.Sha256Length];
            _sha256.GetHashAndReset(hash);
            if (!hash.SequenceEqual(_expectedSha256))
                throw new S3BodyVerificationException(S3BodyErrors.ContentSha256Mismatch);
        }

        if (_checksum is not null)
        {
            Span<byte> digest = stackalloc byte[_checksum.Length];
            _checksum.GetHashAndReset(digest);
            if (!digest.SequenceEqual(_expectedChecksum))
                throw new S3BodyVerificationException(S3BodyErrors.BadDigest);
            VerifiedChecksum = new VerifiedChecksum(_checksum.Algorithm, _expectedChecksum!);
        }
    }
}
