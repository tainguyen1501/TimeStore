using System.Net;
using System.Net.Sockets;

namespace TimeStore.Server.Tests.Infrastructure;

/// <summary>Serves one file over plain HTTP on a free loopback port (404 for anything else): an archive behind a URL.</summary>
public sealed class FileHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public FileHttpServer(string path)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var name = Path.GetFileName(path);
        Url = $"http://127.0.0.1:{port}/{name}";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
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
