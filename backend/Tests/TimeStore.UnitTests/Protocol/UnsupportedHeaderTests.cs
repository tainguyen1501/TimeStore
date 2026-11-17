using TimeStore.Protocol.Http;
using TimeStore.Protocol.Routing;

namespace TimeStore.UnitTests.Protocol;

public sealed class UnsupportedHeaderTests
{
    private static readonly PutObject Put = new("photos", "a.jpg");

    private static string? Check(S3Operation operation, params (string Name, string Value)[] headers)
    {
        var map = headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
        return S3UnsupportedHeaders.Find(operation, map.Keys, name => map.GetValueOrDefault(name))?.Code;
    }

    [Theory]
    [InlineData("x-amz-server-side-encryption", "AES256")]
    [InlineData("x-amz-server-side-encryption", "aws:kms")]
    [InlineData("x-amz-server-side-encryption-aws-kms-key-id", "key")]
    [InlineData("x-amz-server-side-encryption-customer-algorithm", "AES256")]
    [InlineData("x-amz-server-side-encryption-bucket-key-enabled", "true")]
    [InlineData("x-amz-tagging", "a=b")]
    [InlineData("x-amz-object-lock-mode", "GOVERNANCE")]
    [InlineData("x-amz-object-lock-legal-hold", "ON")]
    [InlineData("x-amz-storage-class", "GLACIER")]
    [InlineData("x-amz-website-redirect-location", "/other")]
    [InlineData("X-Amz-Server-Side-Encryption", "AES256")] // header names are case-insensitive
    public void ObjectWrites_RefuseFeaturesThatWouldBeIgnored(string name, string value)
    {
        Assert.Equal(S3HeaderErrors.Codes.NotImplemented, Check(Put, (name, value)));
        Assert.Equal(S3HeaderErrors.Codes.NotImplemented, Check(new CreateMultipartUpload("photos", "a.jpg"), (name, value)));
    }

    [Theory]
    [InlineData("x-amz-acl", "public-read")]
    [InlineData("x-amz-acl", "authenticated-read")]
    [InlineData("x-amz-acl", "private")] // Amazon S3 with ACLs disabled accepts only bucket-owner-full-control
    [InlineData("x-amz-grant-read", "uri=\"http://acs.amazonaws.com/groups/global/AllUsers\"")]
    [InlineData("x-amz-grant-full-control", "id=other")]
    public void ObjectWrites_RefuseAcls_AsABucketWithAclsDisabled(string name, string value) =>
        Assert.Equal(S3HeaderErrors.Codes.AclNotSupported, Check(Put, (name, value)));

    [Theory]
    [InlineData("x-amz-acl", "bucket-owner-full-control")]
    [InlineData("x-amz-acl", "BUCKET-OWNER-FULL-CONTROL")]
    [InlineData("x-amz-storage-class", "STANDARD")]
    [InlineData("x-amz-storage-class", "standard")]
    [InlineData("cache-control", "max-age=60")]
    [InlineData("x-amz-meta-owner", "felix")]
    [InlineData("x-amz-checksum-crc32", "AAAAAA==")]
    public void ObjectWrites_AcceptDefaultsAndOrdinaryHeaders(string name, string value) => Assert.Null(Check(Put, (name, value)));

    [Fact]
    public void Reads_RefuseCustomerKeys_ButNotOtherHeaders()
    {
        var get = new GetObject("photos", "a.jpg", null, null, []);

        Assert.Equal(S3HeaderErrors.Codes.NotImplemented, Check(get, ("x-amz-server-side-encryption-customer-key", "secret")));
        Assert.Equal(S3HeaderErrors.Codes.NotImplemented, Check(new UploadPart("photos", "a.jpg", "u", 1), ("x-amz-server-side-encryption-customer-key-MD5", "x")));
        Assert.Null(Check(get, ("x-amz-tagging", "a=b"))); // meaningless on a read: no reason to refuse
    }

    [Fact]
    public void TheMessage_NamesTheHeader_NeverItsValue()
    {
        var map = new Dictionary<string, string> { ["x-amz-server-side-encryption-customer-key"] = "c2VjcmV0LWtleQ==" };

        var error = S3UnsupportedHeaders.Find(new GetObject("b", "k", null, null, []), map.Keys, map.GetValueOrDefault)!;

        Assert.Contains("x-amz-server-side-encryption-customer-key", error.Message);
        Assert.DoesNotContain("c2VjcmV0LWtleQ==", error.Message);
    }

    [Theory]
    [InlineData("x-amz-bucket-object-lock-enabled", "true", S3HeaderErrors.Codes.NotImplemented)]
    [InlineData("x-amz-bucket-object-lock-enabled", "false", null)]
    [InlineData("x-amz-object-ownership", "ObjectWriter", S3HeaderErrors.Codes.NotImplemented)]
    [InlineData("x-amz-object-ownership", "BucketOwnerEnforced", null)]
    [InlineData("x-amz-acl", "public-read-write", S3HeaderErrors.Codes.BucketAclNotSupported)]
    [InlineData("x-amz-grant-read", "id=other", S3HeaderErrors.Codes.BucketAclNotSupported)]
    [InlineData("x-amz-acl", "private", null)] // owner-only: allowed on CreateBucket
    public void CreateBucket_RefusesLocksOwnershipAndAcls(string name, string value, string? expected) =>
        Assert.Equal(expected, Check(new CreateBucket("photos"), (name, value)));
}
