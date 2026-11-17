using TimeStore.Application.Abstractions;
using TimeStore.Protocol.Auth;

namespace TimeStore.Server.S3;

/// <summary><see cref="ISigV4Request"/> over the ASP.NET request (header names are case-insensitive there).</summary>
internal sealed class HttpSigV4Request(HttpRequest request, string rawTarget) : ISigV4Request
{
    public string Method => request.Method;

    public string RawTarget => rawTarget;

    public string? GetHeader(string name) =>
        request.Headers.TryGetValue(name, out var values) && values.Count > 0 ? values.ToString() : null;

    public IEnumerable<string> HeaderNames
    {
        get
        {
            foreach (var name in request.Headers.Keys)
                yield return name.ToLowerInvariant();
        }
    }
}

/// <summary>Resolves SigV4 secrets through the application's credential store.</summary>
internal sealed class CredentialSecretResolver(ICredentialStore credentials) : ISecretKeyResolver
{
    public async ValueTask<string?> FindSecretKeyAsync(string accessKeyId, CancellationToken ct) =>
        (await credentials.FindAsync(accessKeyId, ct))?.SecretKey;
}
