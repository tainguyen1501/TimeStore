using System.Net;
using System.Net.Sockets;

namespace TimeStore.Server.Tests.Infrastructure;

/// <summary>Serves one file over plain HTTP on a free loopback port (404 for anything else): an archive behind a URL.</summary>
public sealed class FileHttpServer : IAsyncDisposable
{
    private const int StartAttempts = 5;

    private readonly HttpListener _listener;
    private readonly Task _loop;

    public FileHttpServer(string path)
    {
        var name = Path.GetFileName(path);
        for (var attempt = 1; ; attempt++)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            // The probed port may be taken before the listener binds it (see TimeStoreServer): try another one.
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < StartAttempts)
            {
                listener.Close();
                continue;
            }

            _listener = listener;
            Url = $"http://127.0.0.1:{port}/{name}";
            break;
        }

        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // stopped
                }

                if (context.Request.Url!.AbsolutePath == "/" + name && File.Exists(path))
                {
                    await using var file = File.OpenRead(path);
                    await file.CopyToAsync(context.Response.OutputStream);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
        });
    }

    public string Url { get; }

    private int _disposed;

    /// <summary>Stops serving; may be called more than once (a test stops it early, then disposes it).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _listener.Stop();
        _listener.Close();
        await _loop;
    }
}
