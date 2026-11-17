using TimeStore.Application.Features.Policies;

namespace TimeStore.UnitTests.Policies;

/// <summary>Step 7g: "Make public" edits one TimeStore statement and leaves the rest of the policy as written.</summary>
public sealed class PublicAccessTests
{
    private const string Bucket = "media";

    private static BucketPolicy Parse(string? document) => BucketPolicy.Parse(document!, Bucket).Value;

    private static PolicyDecision AnonymousRead(BucketPolicy policy, string key) =>
        policy.Evaluate(new PolicyRequest("s3:GetObject", $"arn:aws:s3:::{Bucket}/{key}", null, new Dictionary<string, string>()));

    [Fact]
    public void Folder_FromNoPolicy_IsReadableByEveryone_OnlyInside()
    {
        var document = PublicAccess.Apply(null, Bucket, "public/", isPublic: true);
        var policy = Parse(document);

        Assert.Equal(["public/"], PublicAccess.Prefixes(document, Bucket));
        Assert.Equal(PolicyDecision.Allow, AnonymousRead(policy, "public/a/b.png"));
        Assert.Equal(PolicyDecision.NotApplicable, AnonymousRead(policy, "private/x"));
        Assert.Equal(PolicyDecision.NotApplicable, AnonymousRead(policy, "publicity.txt")); // folder, not a string prefix
    }

    [Fact]
    public void WholeBucket_IsTheEmptyPrefix()
    {
        var document = PublicAccess.Apply(null, Bucket, "", isPublic: true);
        Assert.Equal([""], PublicAccess.Prefixes(document, Bucket));
        Assert.Equal(PolicyDecision.Allow, AnonymousRead(Parse(document), "anything"));
        Assert.True(Parse(document).IsPublic);
    }

    [Fact]
    public void OtherStatements_AreKept_AndTheLastPrivateRemovesOnlyOurs()
    {
        const string own = """
            {"Version":"2012-10-17","Statement":{"Sid":"Mine","Effect":"Deny","Principal":"*","Action":"s3:DeleteObject","Resource":"arn:aws:s3:::media/*"}}
            """;
        var withPublic = PublicAccess.Apply(own, Bucket, "img/", isPublic: true);
        var both = PublicAccess.Apply(withPublic, Bucket, "docs/", isPublic: true);
        Assert.Equal(["docs/", "img/"], PublicAccess.Prefixes(both, Bucket));
        Assert.Contains("\"Mine\"", both);

        var one = PublicAccess.Apply(both, Bucket, "img/", isPublic: false);
        Assert.Equal(["docs/"], PublicAccess.Prefixes(one, Bucket));
        var none = PublicAccess.Apply(one, Bucket, "docs/", isPublic: false);
        Assert.Empty(PublicAccess.Prefixes(none, Bucket));
        Assert.Contains("\"Mine\"", none);
        Assert.Equal(PolicyDecision.Deny, Parse(none).Evaluate(new PolicyRequest("s3:DeleteObject", "arn:aws:s3:::media/x", "k", new Dictionary<string, string>())));
    }

    [Fact]
    public void LastPublicPrefix_OnAnOtherwiseEmptyPolicy_RemovesThePolicy()
    {
        var document = PublicAccess.Apply(null, Bucket, "a/", isPublic: true);
        Assert.Null(PublicAccess.Apply(document, Bucket, "a/", isPublic: false));
    }

    [Fact]
    public void Repeating_ChangesNothing()
    {
        var document = PublicAccess.Apply(null, Bucket, "a/", isPublic: true);
        Assert.Equal(document, PublicAccess.Apply(document, Bucket, "a/", isPublic: true));
        Assert.Null(PublicAccess.Apply(null, Bucket, "a/", isPublic: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("photos/")]
    [InlineData("a b/ü/")]
    public void ValidPrefixes(string prefix) => Assert.Null(PublicAccess.Validate(prefix));

    [Theory]
    [InlineData("photos")] // not a folder
    [InlineData("/photos/")]
    [InlineData("a*/")] // would be a wildcard
    [InlineData("a?/")]
    [InlineData("a\n/")]
    public void InvalidPrefixes(string prefix) =>
        Assert.Equal(PolicyErrors.Codes.InvalidPublicPrefix, PublicAccess.Validate(prefix)!.Code);
}
