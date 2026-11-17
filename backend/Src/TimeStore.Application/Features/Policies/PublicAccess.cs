using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Policies;

/// <summary>
/// "Make public" for a bucket or a folder (step 7g): one statement in the bucket policy, owned by TimeStore
/// (<see cref="StatementId"/>), allows everyone <c>s3:GetObject</c> on <c>arn:aws:s3:::bucket/&lt;prefix&gt;*</c>. Every
/// other statement is kept as written. The empty prefix is the whole bucket.
/// </summary>
public static class PublicAccess
{
    /// <summary>The Sid of the statement TimeStore adds and removes; other statements are never touched.</summary>
    public const string StatementId = "TimeStorePublicRead";

    private const string Version = "2012-10-17";
    private const string Sid = "Sid";
    private const string AllObjects = "*";
    private const string FolderSeparator = "/";

    /// <summary>The prefixes made public with <see cref="Apply"/>, sorted ("" = the whole bucket).</summary>
    public static IReadOnlyList<string> Prefixes(string? document, string bucket)
    {
        if (document is null || Statements(JsonNode.Parse(document)) is not { } statements
            || Managed(statements) is not { } managed)
            return [];

        var arnPrefix = BucketPolicy.ArnPrefix + bucket + FolderSeparator;
        var prefixes = new List<string>();
        foreach (var resource in Strings(managed[BucketPolicy.Json.Resource]))
        {
            if (resource.StartsWith(arnPrefix, StringComparison.Ordinal) && resource.EndsWith(AllObjects, StringComparison.Ordinal))
                prefixes.Add(resource[arnPrefix.Length..^AllObjects.Length]);
        }

        prefixes.Sort(StringComparer.Ordinal);
        return prefixes;
    }

    /// <summary>
    /// The prefix's validity: empty (the bucket) or a folder ending in '/', without IAM wildcards ('*' and '?' would
    /// make more public than the folder) or control characters.
    /// </summary>
    public static AppError? Validate(string prefix)
    {
        if (prefix.Length == 0)
            return null;
        if (!prefix.EndsWith(FolderSeparator, StringComparison.Ordinal) || prefix.StartsWith(FolderSeparator, StringComparison.Ordinal)
            || prefix.Contains('*') || prefix.Contains('?') || prefix.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(prefix) > 1024)
            return PolicyErrors.InvalidPublicPrefix;
        return null;
    }

    /// <summary>
    /// The document with <paramref name="prefix"/> public or not; null when nothing is left (the policy is removed).
    /// The caller validates the result with <see cref="BucketPolicy.Parse"/>.
    /// </summary>
    public static string? Apply(string? document, string bucket, string prefix, bool isPublic)
    {
        var root = document is null ? new JsonObject { [BucketPolicy.Json.Version] = Version } : JsonNode.Parse(document)!.AsObject();
        var statements = Statements(root) ?? [];
        var prefixes = new SortedSet<string>(Prefixes(document, bucket), StringComparer.Ordinal);
        if (isPublic)
            prefixes.Add(prefix);
        else
            prefixes.Remove(prefix);

        if (Managed(statements) is { } old)
            statements.Remove(old);
        if (prefixes.Count > 0)
        {
            var resources = new JsonArray();
            foreach (var p in prefixes)
                resources.Add((JsonNode)JsonValue.Create(BucketPolicy.ArnPrefix + bucket + FolderSeparator + p + AllObjects));
            statements.Add((JsonNode)new JsonObject
            {
                [Sid] = StatementId,
                [BucketPolicy.Json.Effect] = BucketPolicy.Json.Allow,
                [BucketPolicy.Json.Principal] = AllObjects,
                [BucketPolicy.Json.Action] = S3Actions.Name(S3Actions.GetObject),
                [BucketPolicy.Json.Resource] = resources,
            });
        }

        if (statements.Count == 0)
            return null;
        root[BucketPolicy.Json.Statement] = statements;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            root.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The statement list (a single statement becomes a list of one).</summary>
    private static JsonArray? Statements(JsonNode? root)
    {
        if (root is not JsonObject obj || !obj.TryGetPropertyValue(BucketPolicy.Json.Statement, out var node) || node is null)
            return null;
        if (node is JsonArray array)
            return array;
        obj.Remove(BucketPolicy.Json.Statement);
        return [node];
    }

    private static JsonObject? Managed(JsonArray statements) =>
        statements.OfType<JsonObject>().FirstOrDefault(static s =>
            s.TryGetPropertyValue(Sid, out var sid) && sid is JsonValue value
            && value.TryGetValue<string>(out var text) && text == StatementId);

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        JsonArray array => array.OfType<JsonValue>().Select(static v => v.TryGetValue<string>(out var t) ? t : null).OfType<string>(),
        _ => [],
    };
}
