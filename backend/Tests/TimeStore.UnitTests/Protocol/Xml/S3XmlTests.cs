using System.Text;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Xml;

namespace TimeStore.UnitTests.Protocol.Xml;

public sealed class S3XmlTests
{
    private const string Xmlns = "xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"";
    private static readonly S3Owner Owner = new("timestore-root", "timestore-root");
    private static readonly DateTime Created = new(2026, 11, 16, 9, 30, 15, 123, DateTimeKind.Utc);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static async Task<string> Render(Func<Stream, Task> write)
    {
        using var stream = new MemoryStream();
        await write(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // --- Writers -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAllMyBuckets_MatchesS3Document()
    {
        var xml = await Render(s => S3XmlWriter.ListAllMyBucketsAsync(s, Owner, [new("photos", Created), new("videos", Created)]));

        Assert.Equal(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><ListAllMyBucketsResult {Xmlns}>"
            + "<Owner><ID>timestore-root</ID><DisplayName>timestore-root</DisplayName></Owner><Buckets>"
            + "<Bucket><Name>photos</Name><CreationDate>2026-11-16T09:30:15.123Z</CreationDate></Bucket>"
            + "<Bucket><Name>videos</Name><CreationDate>2026-11-16T09:30:15.123Z</CreationDate></Bucket>"
            + "</Buckets></ListAllMyBucketsResult>",
            xml);
    }

    [Theory]
    [InlineData("us-east-1", $"<LocationConstraint {Xmlns} />")]
    [InlineData("eu-west-1", $"<LocationConstraint {Xmlns}>eu-west-1</LocationConstraint>")]
    public async Task LocationConstraint_IsEmptyForUsEast1(string region, string expected) =>
        Assert.EndsWith(expected, await Render(s => S3XmlWriter.LocationConstraintAsync(s, region)));

    [Fact]
    public async Task VersioningConfiguration_NeverEnabled_HasNoStatus() =>
        Assert.EndsWith($"<VersioningConfiguration {Xmlns} />", await Render(s => S3XmlWriter.VersioningConfigurationAsync(s, null)));

    [Fact]
    public async Task ListObjectsV2_WritesEntriesPrefixesAndPaging()
    {
        var request = new ListObjectsV2("photos", "2026/", "/", "token-1", null, 2, EncodeUrl: false, FetchOwner: false);
        var listing = new S3ObjectListing(
            [new("2026/cat.jpg", Created, "d41d8cd98f00b204e9800998ecf8427e", 0, S3StorageClasses.Standard)],
            ["2026/11/"], IsTruncated: true, NextToken: "token-2");

        var xml = await Render(s => S3XmlWriter.ListObjectsV2Async(s, request, listing, Owner));

        Assert.Contains(
            $"<ListBucketResult {Xmlns}><Name>photos</Name><Prefix>2026/</Prefix><KeyCount>2</KeyCount><MaxKeys>2</MaxKeys>"
            + "<Delimiter>/</Delimiter><IsTruncated>true</IsTruncated><ContinuationToken>token-1</ContinuationToken>"
            + "<NextContinuationToken>token-2</NextContinuationToken>"
            + "<Contents><Key>2026/cat.jpg</Key><LastModified>2026-11-16T09:30:15.123Z</LastModified>"
            + "<ETag>\"d41d8cd98f00b204e9800998ecf8427e\"</ETag><Size>0</Size><StorageClass>STANDARD</StorageClass></Contents>"
            + "<CommonPrefixes><Prefix>2026/11/</Prefix></CommonPrefixes></ListBucketResult>",
            xml);
    }

    [Fact]
    public async Task ListObjectsV2_EncodingTypeUrl_EncodesKeysAndPrefixes()
    {
        var request = new ListObjectsV2("photos", "a b/", "/", null, null, 1000, EncodeUrl: true, FetchOwner: true);
        var listing = new S3ObjectListing([new("a b/ảnh+1.jpg", Created, "e", 1, S3StorageClasses.Standard)], [], false, null);

        var xml = await Render(s => S3XmlWriter.ListObjectsV2Async(s, request, listing, Owner));

        Assert.Contains("<Prefix>a%20b/</Prefix>", xml);
        Assert.Contains("<EncodingType>url</EncodingType>", xml);
        Assert.Contains("<Key>a%20b/%E1%BA%A3nh%2B1.jpg</Key>", xml);
        Assert.Contains("<Owner><ID>timestore-root</ID>", xml); // fetch-owner=true
    }

    [Fact]
    public async Task ListObjectsV1_AlwaysWritesOwnerAndMarker()
    {
        var request = new ListObjects("photos", null, null, null, 1000, EncodeUrl: false);
        var listing = new S3ObjectListing([new("k", Created, "e", 1, S3StorageClasses.Standard)], [], false, null);

        var xml = await Render(s => S3XmlWriter.ListObjectsAsync(s, request, listing, Owner));

        Assert.Contains("<Name>photos</Name><Prefix /><Marker /><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>", xml);
        Assert.Contains("<Owner><ID>timestore-root</ID>", xml);
    }

    [Fact]
    public async Task ListObjectsV1_EchoesThePrefixUnencoded_EvenWithUrlEncoding_ButEncodesKeys()
    {
        // Amazon S3 leaves the top-level Prefix of version 1 as sent; SDKs decode it only in version 2.
        var request = new ListObjects("photos", "\n a b/", null, null, 1000, EncodeUrl: true);
        var listing = new S3ObjectListing([new("\n a b/c d", Created, "e", 1, S3StorageClasses.Standard)], [], false, null);

        var xml = await Render(s => S3XmlWriter.ListObjectsAsync(s, request, listing, Owner));

        Assert.Contains("<Prefix>\n a b/</Prefix>", xml);
        Assert.Contains("<Key>%0A%20a%20b/c%20d</Key>", xml);
    }

    [Fact]
    public async Task ListObjectsV1_EncodesAPrefixXmlCannotCarry()
    {
        var request = new ListObjects("photos", "\u0001", null, null, 1000, EncodeUrl: true);

        var xml = await Render(s => S3XmlWriter.ListObjectsAsync(s, request, new S3ObjectListing([], [], false, null), Owner));

        Assert.Contains("<Prefix>%01</Prefix>", xml);
    }

    [Fact]
    public async Task Writers_EscapeXmlSpecialCharacters()
    {
        var listing = new S3ObjectListing([new("a<&>\"'.txt", Created, "e", 1, S3StorageClasses.Standard)], [], false, null);

        var xml = await Render(s => S3XmlWriter.ListObjectsV2Async(
            s, new ListObjectsV2("photos", null, null, null, null, 1000, false, false), listing, Owner));

        Assert.Contains("<Key>a&lt;&amp;&gt;\"'.txt</Key>", xml);
    }

    // --- Readers -------------------------------------------------------------------------------------------------

    private async Task<string?> ReadLocation(string xml)
    {
        var result = await S3XmlReader.ReadCreateBucketConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)), _ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private async Task<string> ReadError(string xml)
    {
        var result = await S3XmlReader.ReadCreateBucketConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)), _ct);
        Assert.True(result.IsFailure);
        return result.Error.Code;
    }

    [Theory]
    [InlineData($"<CreateBucketConfiguration {Xmlns}><LocationConstraint>eu-west-1</LocationConstraint></CreateBucketConfiguration>")]
    [InlineData("<CreateBucketConfiguration><LocationConstraint> eu-west-1 </LocationConstraint></CreateBucketConfiguration>")]
    public async Task CreateBucketConfiguration_ReadsLocation(string xml) => Assert.Equal("eu-west-1", await ReadLocation(xml));

    [Theory]
    [InlineData("")]
    [InlineData("<CreateBucketConfiguration />")]
    [InlineData("<CreateBucketConfiguration><LocationConstraint></LocationConstraint></CreateBucketConfiguration>")]
    public async Task CreateBucketConfiguration_WithoutLocation_IsNull(string xml) => Assert.Null(await ReadLocation(xml));

    [Theory]
    [InlineData("<CreateBucketConfiguration><LocationConstraint>eu</CreateBucketConfiguration>")]
    [InlineData("<Something />")]
    [InlineData("not xml")]
    public async Task CreateBucketConfiguration_Malformed_IsMalformedXml(string xml) =>
        Assert.Equal(S3XmlErrors.Codes.MalformedXml, await ReadError(xml));

    [Fact]
    public async Task CreateBucketConfiguration_WithDtd_IsRejected() => // XXE / billion laughs
        Assert.Equal(S3XmlErrors.Codes.MalformedXml, await ReadError(
            "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>"
            + "<CreateBucketConfiguration><LocationConstraint>&e;</LocationConstraint></CreateBucketConfiguration>"));

    [Fact]
    public async Task CreateBucketConfiguration_Oversized_IsRejected() =>
        Assert.Equal(S3XmlErrors.Codes.MalformedXml, await ReadError(
            "<CreateBucketConfiguration><LocationConstraint>" + new string('x', S3XmlReader.MaxSmallDocumentBytes)
            + "</LocationConstraint></CreateBucketConfiguration>"));
}
