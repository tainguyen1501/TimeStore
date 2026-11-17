using System.Globalization;
using System.Net;
using System.Text.Json;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Policies;

/// <summary>What a bucket policy says about one request.</summary>
public enum PolicyDecision
{
    /// <summary>No statement applies: the caller's own permissions decide.</summary>
    NotApplicable,
    Allow,
    Deny,
}

/// <summary>
/// One request as a policy sees it: the action (<c>s3:GetObject</c>), the resource ARN, the caller (null when
/// anonymous) and the condition context (lower-case keys).
/// </summary>
public sealed record PolicyRequest(
    string Action, string Resource, string? AccessKeyId, IReadOnlyDictionary<string, string> Context);

/// <summary>
/// A parsed, validated bucket policy (docs/research/s3-bucket-policy.md). Evaluation follows IAM: an explicit Deny wins,
/// then an Allow; otherwise the policy does not apply.
/// </summary>
public sealed class BucketPolicy
{
    public const int MaxDocumentBytes = 20 * 1024;
    public const string ArnPrefix = "arn:aws:s3:::";
    private const string Wildcard = "*";
    private const string ActionPrefix = "s3:";
    private const string UserArnMarker = ":user/";
    private const string IfExists = "IfExists";

    private static readonly string[] Versions = ["2012-10-17", "2008-10-17"];

    /// <summary>Element names and fixed values of the policy language.</summary>
    internal static class Json
    {
        public const string Version = "Version";
        public const string Statement = "Statement";
        public const string Effect = "Effect";
        public const string Allow = "Allow";
        public const string Deny = "Deny";
        public const string Principal = "Principal";
        public const string NotPrincipal = "NotPrincipal";
        public const string Action = "Action";
        public const string NotAction = "NotAction";
        public const string Resource = "Resource";
        public const string NotResource = "NotResource";
        public const string Condition = "Condition";
        public const string Aws = "AWS";
        public const string IamArnPrefix = "arn:aws:iam::";
    }

    private BucketPolicy(IReadOnlyList<Statement> statements) => Statements = statements;

    private IReadOnlyList<Statement> Statements { get; }

    /// <summary>An Allow for everyone (<c>*</c>) without conditions: the bucket is public.</summary>
    public bool IsPublic => Statements.Any(static s => s.Allow && s.Principals.Contains(Wildcard) && s.Conditions.Count == 0);

    public PolicyDecision Evaluate(PolicyRequest request)
    {
        var allowed = false;
        foreach (var statement in Statements)
        {
            if (!statement.Matches(request))
                continue;
            if (!statement.Allow)
                return PolicyDecision.Deny;
            allowed = true;
        }

        return allowed ? PolicyDecision.Allow : PolicyDecision.NotApplicable;
    }

    /// <summary>Parses and validates a policy for <paramref name="bucket"/>; errors are <c>POLICY_*</c> catalog entries.</summary>
    public static Result<BucketPolicy> Parse(string document, string bucket)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(document) > MaxDocumentBytes)
            return PolicyErrors.Malformed($"Policies must be at most {MaxDocumentBytes} bytes.");
        try
        {
            using var json = JsonDocument.Parse(document);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PolicyErrors.Malformed("A policy is a JSON object.");
            if (!root.TryGetProperty(Json.Version, out var version) || !Versions.Contains(version.GetString()))
                return PolicyErrors.Malformed($"Version must be {string.Join(" or ", Versions)}.");
            if (!root.TryGetProperty(Json.Statement, out var statementsElement))
                return PolicyErrors.Malformed("Missing required field Statement.");

            var statements = new List<Statement>();
            foreach (var element in OneOrMany(statementsElement))
            {
                var parsed = ParseStatement(element, bucket);
                if (parsed.IsFailure)
                    return parsed.Error;
                statements.Add(parsed.Value);
            }

            return statements.Count == 0 ? PolicyErrors.Malformed("Missing required field Statement.") : new BucketPolicy(statements);
        }
        catch (JsonException)
        {
            return PolicyErrors.Malformed("This policy contains invalid Json.");
        }
        catch (InvalidOperationException)
        {
            return PolicyErrors.Malformed("A policy field has the wrong type.");
        }
    }

    private static Result<Statement> ParseStatement(JsonElement element, string bucket)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return PolicyErrors.Malformed("A statement is a JSON object.");
        if (element.TryGetProperty(Json.NotPrincipal, out _))
            return PolicyErrors.NotPrincipal;

        var effect = element.TryGetProperty(Json.Effect, out var e) ? e.GetString() : null;
        if (effect is not (Json.Allow or Json.Deny))
            return PolicyErrors.Malformed("Effect must be Allow or Deny.");

        if (!element.TryGetProperty(Json.Principal, out var principalElement))
            return PolicyErrors.Malformed("Missing required field Principal.");
        var principals = ParsePrincipals(principalElement);
        if (principals is null)
            return PolicyErrors.Malformed("Invalid principal in policy: use \"*\", an access key id or arn:aws:iam::<account>:user/<access key id>.");

        var (actions, notAction) = Either(element, Json.Action, Json.NotAction);
        if (actions is null)
            return PolicyErrors.Malformed("Missing required field Action.");
        foreach (var action in actions)
        {
            if (action != Wildcard && !action.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
                return PolicyErrors.Malformed($"Policy has invalid action: {action}.");
            if (!action.Contains('*') && !action.Contains('?') && action != Wildcard && !S3Actions.Known.Contains(action[ActionPrefix.Length..]))
                return PolicyErrors.Malformed($"Policy has invalid action: {action}.");
        }

        var (resources, notResource) = Either(element, Json.Resource, Json.NotResource);
        if (resources is null)
            return PolicyErrors.Malformed("Missing required field Resource.");
        foreach (var resource in resources)
        {
            if (!resource.StartsWith(ArnPrefix, StringComparison.Ordinal))
                return PolicyErrors.Malformed($"Policy has invalid resource: {resource}.");
            var bucketPart = resource[ArnPrefix.Length..].Split('/', 2)[0];
            if (!Glob(bucketPart, bucket, ignoreCase: false))
                return PolicyErrors.Malformed($"Policy has invalid resource: {resource} does not name this bucket.");
        }

        var conditions = new List<Condition>();
        if (element.TryGetProperty(Json.Condition, out var conditionElement))
        {
            if (conditionElement.ValueKind != JsonValueKind.Object)
                return PolicyErrors.Malformed("Condition is a JSON object.");
            foreach (var op in conditionElement.EnumerateObject())
            {
                var name = op.Name;
                var ifExists = name.EndsWith(IfExists, StringComparison.Ordinal);
                var baseName = ifExists ? name[..^IfExists.Length] : name;
                if (!Operators.Contains(baseName))
                    return PolicyErrors.Malformed($"Invalid Condition type: {name}.");
                if (op.Value.ValueKind != JsonValueKind.Object)
                    return PolicyErrors.Malformed($"Condition {name} needs key-value pairs.");
                foreach (var pair in op.Value.EnumerateObject())
                {
                    var key = pair.Name.ToLowerInvariant();
                    if (!ConditionKeys.Contains(key))
                        return PolicyErrors.Malformed($"Policy has an unsupported condition key: {pair.Name}.");
                    var values = OneOrMany(pair.Value).Select(ScalarText).ToArray();
                    if (baseName.StartsWith("Ip", StringComparison.Ordinal) || baseName.StartsWith("NotIp", StringComparison.Ordinal))
                    {
                        if (values.Any(v => !TryCidr(v, out _, out _)))
                            return PolicyErrors.Malformed($"Condition {name} needs IP addresses or CIDR ranges.");
                    }

                    conditions.Add(new Condition(baseName, ifExists, key, values));
                }
            }
        }

        return new Statement(effect == Json.Allow, principals, actions, notAction, resources, notResource, conditions);
    }

    private static IReadOnlyList<string>? ParsePrincipals(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() == Wildcard ? [Wildcard] : null;
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var principals = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name != Json.Aws)
                return null; // Service, Federated, CanonicalUser: nothing in TimeStore matches them
            foreach (var value in OneOrMany(property.Value))
            {
                var text = value.GetString();
                if (string.IsNullOrEmpty(text))
                    return null;
                if (text == Wildcard)
                    principals.Add(Wildcard);
                else if (text.StartsWith(Json.IamArnPrefix, StringComparison.Ordinal) && text.LastIndexOf(UserArnMarker, StringComparison.Ordinal) is var at and > 0)
                    principals.Add(text[(at + UserArnMarker.Length)..]);
                else if (!text.Contains(':'))
                    principals.Add(text);
                else
                    return null;
            }
        }

        return principals.Count == 0 ? null : principals;
    }

    private static (IReadOnlyList<string>? Values, bool Negated) Either(JsonElement element, string name, string negated)
    {
        if (element.TryGetProperty(name, out var value))
            return (OneOrMany(value).Select(static v => v.GetString() ?? string.Empty).ToArray(), false);
        if (element.TryGetProperty(negated, out var not))
            return (OneOrMany(not).Select(static v => v.GetString() ?? string.Empty).ToArray(), true);
        return (null, false);
    }

    private static IEnumerable<JsonElement> OneOrMany(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToArray() : [element];

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static readonly HashSet<string> Operators =
    [
        "StringEquals", "StringNotEquals", "StringLike", "StringNotLike", "IpAddress", "NotIpAddress", "Bool",
        "NumericEquals", "NumericNotEquals", "NumericLessThan", "NumericLessThanEquals", "NumericGreaterThan",
        "NumericGreaterThanEquals",
    ];

    private static readonly HashSet<string> ConditionKeys =
    [
        PolicyContextKeys.SourceIp, PolicyContextKeys.SecureTransport, PolicyContextKeys.Username,
        PolicyContextKeys.Prefix, PolicyContextKeys.Delimiter, PolicyContextKeys.MaxKeys, PolicyContextKeys.VersionId,
    ];

    /// <summary><c>*</c> any run, <c>?</c> one character.</summary>
    public static bool Glob(string pattern, string text, bool ignoreCase)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t], ignoreCase)))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;

        static bool Same(char a, char b, bool ignoreCase) =>
            ignoreCase ? char.ToLowerInvariant(a) == char.ToLowerInvariant(b) : a == b;
    }

    private static bool TryCidr(string text, out IPAddress address, out int prefixLength)
    {
        var slash = text.IndexOf('/');
        var host = slash < 0 ? text : text[..slash];
        prefixLength = -1;
        if (!IPAddress.TryParse(host, out address!))
            return false;
        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0)
        {
            prefixLength = max;
            return true;
        }

        return int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength)
            && prefixLength >= 0 && prefixLength <= max;
    }

    private static bool InCidr(string cidr, string ip)
    {
        if (!TryCidr(cidr, out var network, out var length) || !IPAddress.TryParse(ip, out var address))
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (network.AddressFamily != address.AddressFamily)
            return false;
        var a = network.GetAddressBytes();
        var b = address.GetAddressBytes();
        for (var i = 0; i < a.Length && length > 0; i++, length -= 8)
        {
            var mask = length >= 8 ? 0xFF : (byte)(0xFF << (8 - length));
            if ((a[i] & mask) != (b[i] & mask))
                return false;
        }

        return true;
    }

    private sealed record Statement(
        bool Allow,
        IReadOnlyList<string> Principals,
        IReadOnlyList<string> Actions,
        bool NotAction,
        IReadOnlyList<string> Resources,
        bool NotResource,
        IReadOnlyList<Condition> Conditions)
    {
        public bool Matches(PolicyRequest request)
        {
            if (!Principals.Any(p => p == Wildcard || (request.AccessKeyId is { } id && p == id)))
                return false;
            if (Actions.Any(a => Glob(a, request.Action, ignoreCase: true)) == NotAction)
                return false;
            if (Resources.Any(r => Glob(r, request.Resource, ignoreCase: false)) == NotResource)
                return false;
            return Conditions.All(c => c.Holds(request.Context));
        }
    }

    private sealed record Condition(string Operator, bool IfExists, string Key, IReadOnlyList<string> Values)
    {
        private bool Negated => Operator.Contains("Not", StringComparison.Ordinal);

        public bool Holds(IReadOnlyDictionary<string, string> context)
        {
            // A missing key fails the condition, except for "...IfExists" and the negated operators (IAM rules).
            if (!context.TryGetValue(Key, out var actual))
                return IfExists || Negated;

            return Operator switch
            {
                "StringEquals" => Values.Any(v => v == actual),
                "StringNotEquals" => Values.All(v => v != actual),
                "StringLike" => Values.Any(v => Glob(v, actual, ignoreCase: false)),
                "StringNotLike" => Values.All(v => !Glob(v, actual, ignoreCase: false)),
                "IpAddress" => Values.Any(v => InCidr(v, actual)),
                "NotIpAddress" => Values.All(v => !InCidr(v, actual)),
                "Bool" => Values.Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase)),
                _ => Numeric(actual),
            };
        }

        private bool Numeric(string actual)
        {
            if (!decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                return false;
            return Values.Any(v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var limit) && Operator switch
            {
                "NumericEquals" => number == limit,
                "NumericNotEquals" => number != limit,
                "NumericLessThan" => number < limit,
                "NumericLessThanEquals" => number <= limit,
                "NumericGreaterThan" => number > limit,
                "NumericGreaterThanEquals" => number >= limit,
                _ => false,
            });
        }
    }
}

/// <summary>Condition keys TimeStore fills in for a request (lower case, as IAM compares them).</summary>
public static class PolicyContextKeys
{
    public const string SourceIp = "aws:sourceip";
    public const string SecureTransport = "aws:securetransport";
    public const string Username = "aws:username";
    public const string Prefix = "s3:prefix";
    public const string Delimiter = "s3:delimiter";
    public const string MaxKeys = "s3:max-keys";
    public const string VersionId = "s3:versionid";
}

/// <summary>The IAM action names of the S3 operations TimeStore has (without the <c>s3:</c> prefix).</summary>
public static class S3Actions
{
    public const string ListAllMyBuckets = "ListAllMyBuckets";
    public const string CreateBucket = "CreateBucket";
    public const string DeleteBucket = "DeleteBucket";
    public const string ListBucket = "ListBucket";
    public const string ListBucketVersions = "ListBucketVersions";
    public const string ListBucketMultipartUploads = "ListBucketMultipartUploads";
    public const string GetBucketLocation = "GetBucketLocation";
    public const string GetBucketVersioning = "GetBucketVersioning";
    public const string PutBucketVersioning = "PutBucketVersioning";
    public const string GetLifecycleConfiguration = "GetLifecycleConfiguration";
    public const string PutLifecycleConfiguration = "PutLifecycleConfiguration";
    public const string GetBucketPolicy = "GetBucketPolicy";
    public const string PutBucketPolicy = "PutBucketPolicy";
    public const string DeleteBucketPolicy = "DeleteBucketPolicy";
    public const string GetBucketPolicyStatus = "GetBucketPolicyStatus";
    public const string GetObject = "GetObject";
    public const string GetObjectVersion = "GetObjectVersion";
    public const string PutObject = "PutObject";
    public const string DeleteObject = "DeleteObject";
    public const string DeleteObjectVersion = "DeleteObjectVersion";
    public const string AbortMultipartUpload = "AbortMultipartUpload";
    public const string ListMultipartUploadParts = "ListMultipartUploadParts";

    public static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ListAllMyBuckets, CreateBucket, DeleteBucket, ListBucket, ListBucketVersions, ListBucketMultipartUploads,
        GetBucketLocation, GetBucketVersioning, PutBucketVersioning, GetLifecycleConfiguration, PutLifecycleConfiguration,
        GetBucketPolicy, PutBucketPolicy, DeleteBucketPolicy, GetBucketPolicyStatus, GetObject, GetObjectVersion, PutObject,
        DeleteObject, DeleteObjectVersion, AbortMultipartUpload, ListMultipartUploadParts,
    };

    public static string Name(string action) => "s3:" + action;
}

public static class PolicyErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string Malformed = "POLICY_MALFORMED";
        public const string NotFound = "POLICY_NOT_FOUND";
        public const string NotImplemented = "POLICY_NOT_IMPLEMENTED";
        public const string AccessDenied = "POLICY_ACCESS_DENIED";
        public const string InvalidPublicPrefix = "POLICY_INVALID_PUBLIC_PREFIX";
        public const string Conflict = "POLICY_CONFLICT";
    }

    public static readonly AppError InvalidPublicPrefix = AppError.Validation(
        Codes.InvalidPublicPrefix, "Only a whole bucket (empty prefix) or a folder (ending in '/', without '*' or '?') can be made public.");

    public static readonly AppError Conflict = AppError.Conflict(
        Codes.Conflict, "The bucket policy kept changing while it was edited; try again.");

    public static AppError Malformed(string reason) => AppError.Validation(Codes.Malformed, reason);

    public static readonly AppError NotFound = AppError.NotFound(Codes.NotFound, "The bucket policy does not exist");

    public static readonly AppError NotPrincipal =
        AppError.Validation(Codes.NotImplemented, "NotPrincipal is not implemented; use Principal with a Deny statement.");

    public static readonly AppError AccessDenied = AppError.Forbidden(Codes.AccessDenied, "Access Denied");
}
