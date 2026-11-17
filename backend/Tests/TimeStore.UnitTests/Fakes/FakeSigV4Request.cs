using TimeStore.Protocol.Auth;

namespace TimeStore.UnitTests.Fakes;

/// <summary>An in-memory request for SigV4 tests. Header names are lower case.</summary>
public sealed class FakeSigV4Request(string method, string rawTarget, Dictionary<string, string> headers) : ISigV4Request
{
    public string Method => method;

    public string RawTarget => rawTarget;

    public Dictionary<string, string> Headers => headers;

    public IEnumerable<string> HeaderNames => headers.Keys;

    public string? GetHeader(string name) => headers.GetValueOrDefault(name);

    public FakeSigV4Request With(string name, string value) =>
        new(method, rawTarget, new Dictionary<string, string>(headers) { [name] = value });

    public FakeSigV4Request Without(string name)
    {
        var copy = new Dictionary<string, string>(headers);
        copy.Remove(name);
        return new(method, rawTarget, copy);
    }

    public FakeSigV4Request WithTarget(string target) => new(method, target, headers);
}

public sealed class DictionarySecretResolver(Dictionary<string, string> secrets) : ISecretKeyResolver
{
    public ValueTask<string?> FindSecretKeyAsync(string accessKeyId, CancellationToken ct) =>
        ValueTask.FromResult(secrets.GetValueOrDefault(accessKeyId));
}
