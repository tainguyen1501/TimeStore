using TimeStore.Application.Features.Policies;

namespace TimeStore.UnitTests.Policies;

/// <summary>Step 6d: bucket policy parsing (what is refused) and IAM-style evaluation.</summary>
public sealed class BucketPolicyTests
{
    private const string Bucket = "photos";

    private static BucketPolicy Parse(string statements) =>
        BucketPolicy.Parse($$"""{"Version":"2012-10-17","Statement":[{{statements}}]}""", Bucket).Value;

    private static string Error(string document) => BucketPolicy.Parse(document, Bucket).Error.Code;

    private static PolicyDecision Decide(
        BucketPolicy policy, string action, string resource, string? key = null, Dictionary<string, string>? context = null) =>
        policy.Evaluate(new PolicyRequest(action, resource, key, context ?? new Dictionary<string, string>()));

    private const string PublicRead =
        """{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::photos/*"}""";

    [Fact]
    public void PublicRead_AllowsAnonymousGetObject_Only()
    {
        var policy = Parse(PublicRead);
        Assert.True(policy.IsPublic);
        Assert.Equal(PolicyDecision.Allow, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/cat.jpg"));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:PutObject", "arn:aws:s3:::photos/cat.jpg"));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:ListBucket", "arn:aws:s3:::photos"));
    }

    [Fact]
    public void Deny_WinsOverAllow()
    {
        var policy = Parse(PublicRead + """,{"Effect":"Deny","Principal":{"AWS":"AKBAD"},"Action":"s3:*","Resource":"arn:aws:s3:::photos/*"}""");
        Assert.Equal(PolicyDecision.Deny, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/a", "AKBAD"));
        Assert.Equal(PolicyDecision.Allow, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/a", "AKGOOD"));
    }

    [Theory]
    [InlineData("\"AKONE\"")]
    [InlineData("{\"AWS\":\"AKONE\"}")]
    [InlineData("{\"AWS\":[\"AKOTHER\",\"arn:aws:iam::123456789012:user/AKONE\"]}")]
    public void Principals_NameAccessKeys(string principal)
    {
        var policy = Parse($$"""{"Effect":"Allow","Principal":{{(principal.StartsWith('"') ? "{\"AWS\":" + principal + "}" : principal)}},"Action":"s3:ListBucket","Resource":"arn:aws:s3:::photos"}""");
        Assert.False(policy.IsPublic);
        Assert.Equal(PolicyDecision.Allow, Decide(policy, "s3:ListBucket", "arn:aws:s3:::photos", "AKONE"));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:ListBucket", "arn:aws:s3:::photos", null));
    }

    [Fact]
    public void Wildcards_InActionsAndResources()
    {
        var policy = Parse("""{"Effect":"Allow","Principal":"*","Action":["s3:Get*","s3:List?ucket"],"Resource":["arn:aws:s3:::photos","arn:aws:s3:::photos/public/*"]}""");
        Assert.Equal(PolicyDecision.Allow, Decide(policy, "s3:GetObjectVersion", "arn:aws:s3:::photos/public/a/b"));
        Assert.Equal(PolicyDecision.Allow, Decide(policy, "s3:ListBucket", "arn:aws:s3:::photos"));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/private/a"));
    }

    [Fact]
    public void NotAction_AndNotResource_Invert()
    {
        var policy = Parse("""{"Effect":"Deny","Principal":"*","NotAction":"s3:GetObject","NotResource":"arn:aws:s3:::photos/open/*"}""");
        Assert.Equal(PolicyDecision.Deny, Decide(policy, "s3:PutObject", "arn:aws:s3:::photos/a"));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/a"));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:PutObject", "arn:aws:s3:::photos/open/a"));
    }

    [Fact]
    public void Conditions_SourceIpAndTransport()
    {
        var policy = Parse("""{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::photos/*","Condition":{"IpAddress":{"aws:SourceIp":"10.0.0.0/8"},"Bool":{"aws:SecureTransport":"false"}}}""");
        Assert.False(policy.IsPublic); // conditional
        var inside = new Dictionary<string, string> { [PolicyContextKeys.SourceIp] = "10.1.2.3", [PolicyContextKeys.SecureTransport] = "false" };
        var outside = new Dictionary<string, string> { [PolicyContextKeys.SourceIp] = "192.168.1.1", [PolicyContextKeys.SecureTransport] = "false" };
        Assert.Equal(PolicyDecision.Allow, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/a", context: inside));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:GetObject", "arn:aws:s3:::photos/a", context: outside));
    }

    [Fact]
    public void Conditions_MissingKey_FailsUnlessIfExistsOrNegated()
    {
        var like = Parse("""{"Effect":"Allow","Principal":"*","Action":"s3:ListBucket","Resource":"arn:aws:s3:::photos","Condition":{"StringLike":{"s3:prefix":"home/*"}}}""");
        var ifExists = Parse("""{"Effect":"Allow","Principal":"*","Action":"s3:ListBucket","Resource":"arn:aws:s3:::photos","Condition":{"StringLikeIfExists":{"s3:prefix":"home/*"}}}""");
        var notEquals = Parse("""{"Effect":"Deny","Principal":"*","Action":"s3:ListBucket","Resource":"arn:aws:s3:::photos","Condition":{"StringNotEquals":{"s3:prefix":"home/"}}}""");
        Assert.Equal(PolicyDecision.NotApplicable, Decide(like, "s3:ListBucket", "arn:aws:s3:::photos"));
        Assert.Equal(PolicyDecision.Allow, Decide(like, "s3:ListBucket", "arn:aws:s3:::photos", context: new() { [PolicyContextKeys.Prefix] = "home/x" }));
        Assert.Equal(PolicyDecision.Allow, Decide(ifExists, "s3:ListBucket", "arn:aws:s3:::photos"));
        Assert.Equal(PolicyDecision.Deny, Decide(notEquals, "s3:ListBucket", "arn:aws:s3:::photos"));
    }

    [Fact]
    public void Conditions_Numeric()
    {
        var policy = Parse("""{"Effect":"Deny","Principal":"*","Action":"s3:ListBucket","Resource":"arn:aws:s3:::photos","Condition":{"NumericGreaterThan":{"s3:max-keys":"100"}}}""");
        Assert.Equal(PolicyDecision.Deny, Decide(policy, "s3:ListBucket", "arn:aws:s3:::photos", context: new() { [PolicyContextKeys.MaxKeys] = "1000" }));
        Assert.Equal(PolicyDecision.NotApplicable, Decide(policy, "s3:ListBucket", "arn:aws:s3:::photos", context: new() { [PolicyContextKeys.MaxKeys] = "10" }));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Statement":[]}""")] // no version
    [InlineData("""{"Version":"2001-01-01","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::photos/*"}]}""")]
    [InlineData("""{"Version":"2012-10-17","Statement":[]}""")] // no statement
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Maybe","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::photos/*"}]}""")]
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:Fly","Resource":"arn:aws:s3:::photos/*"}]}""")]
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::other/*"}]}""")] // another bucket
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"photos/*"}]}""")] // not an ARN
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::photos/*","Condition":{"DateGreaterThan":{"aws:CurrentTime":"2020-01-01"}}}]}""")] // unknown operator
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::photos/*","Condition":{"StringEquals":{"aws:Referer":"x"}}}]}""")] // unknown key
    public void InvalidDocuments_AreMalformed(string document) =>
        Assert.Equal(PolicyErrors.Codes.Malformed, Error(document));

    [Fact]
    public void NotPrincipal_IsNotImplemented() =>
        Assert.Equal(PolicyErrors.Codes.NotImplemented, Error("""{"Version":"2012-10-17","Statement":[{"Effect":"Deny","NotPrincipal":{"AWS":"AKONE"},"Action":"s3:*","Resource":"arn:aws:s3:::photos/*"}]}"""));

    [Fact]
    public void TooLargeDocuments_AreMalformed() =>
        Assert.Equal(PolicyErrors.Codes.Malformed, Error("{\"Version\":\"2012-10-17\",\"Id\":\"" + new string('x', BucketPolicy.MaxDocumentBytes) + "\",\"Statement\":[]}"));

    [Fact]
    public void OlderVersion_AndWildcardBucketResource_AreAccepted()
    {
        var parsed = BucketPolicy.Parse("""{"Version":"2008-10-17","Statement":{"Effect":"Allow","Principal":{"AWS":"*"},"Action":"s3:*","Resource":"arn:aws:s3:::pho*"}}""", Bucket);
        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.Value.IsPublic);
    }

    [Theory]
    [InlineData("a*c", "abbbc", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    [InlineData("*", "", true)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*b*c", "axxbyy", false)]
    public void Glob_MatchesIamWildcards(string pattern, string text, bool expected) =>
        Assert.Equal(expected, BucketPolicy.Glob(pattern, text, ignoreCase: false));
}
