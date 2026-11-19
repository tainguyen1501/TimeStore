using TimeStore.Application.Features.Backups;
using TimeStore.Primitives;

namespace TimeStore.Infra.Backups;

/// <summary>
/// Where a restore reads its archive: a file, or an <c>http(s)://</c> URL read as it downloads (a presigned S3 or R2
/// URL, an archive served by another TimeStore, any web server). Nothing is written to disk before the restore reads
/// it; the restore's own checks (manifest, checksums, Zstandard frame) catch a cut or damaged download.
/// </summary>
public static class ArchiveSource
{
    private const string HttpScheme = "http://";
    private const string HttpsScheme = "https://";
    private const int FileBufferSize = 1 << 20;

    /// <summary>How long the server may take to start answering; the transfer itself has no time limit.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(2);

    // One client for the process (the restore command runs once); no overall timeout: large archives take long.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static bool IsUrl(string archive) =>
        archive.StartsWith(HttpScheme, StringComparison.OrdinalIgnoreCase)
        || archive.StartsWith(HttpsScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to print for <paramref name="archive"/>: a URL without its query and fragment, since a presigned URL's
    /// query is a credential.
    /// </summary>
    public static string DisplayName(string archive) =>
        IsUrl(archive) && Uri.TryCreate(archive, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : Path.GetFileName(archive);

    /// <summary>The archive's bytes, from the start; the caller disposes the stream.</summary>
    public static async ValueTask<Result<Stream>> OpenAsync(string archive, CancellationToken ct)
    {
        if (!IsUrl(archive))
        {
            if (!File.Exists(archive))
                return BackupErrors.RestoreArchiveUnavailable(DisplayName(archive), "the file does not exist.");
            return new FileStream(archive, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = FileBufferSize,
            });
        }

        if (!Uri.TryCreate(archive, UriKind.Absolute, out var uri))
            return BackupErrors.RestoreArchiveUnavailable(DisplayName(archive), "the URL is not valid.");

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waiting.CancelAfter(ResponseTimeout);
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, waiting.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Never the exception's message: it may quote the URL, and a presigned URL's query is a credential.
            return BackupErrors.RestoreArchiveUnavailable(DisplayName(archive), exception is HttpRequestException failed
                ? $"the request failed ({failed.HttpRequestError})."
                : $"no answer within {ResponseTimeout.TotalSeconds:0} s.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            return BackupErrors.RestoreArchiveUnavailable(DisplayName(archive), $"the server answered {status}.");
        }

        return Result<Stream>.Ok(new OwnedResponseStream(response, await response.Content.ReadAsStreamAsync(ct)));
    }

    /// <summary>The body of a response that is disposed with it.</summary>
    private sealed class OwnedResponseStream(HttpResponseMessage response, Stream body) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => body.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            body.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            body.ReadAsync(buffer, offset, count, cancellationToken);

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
                body.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await body.DisposeAsync();
            response.Dispose();
        }
    }
}
