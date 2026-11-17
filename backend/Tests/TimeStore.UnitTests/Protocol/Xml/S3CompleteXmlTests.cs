using System.Text;
using TimeStore.Protocol.Xml;

namespace TimeStore.UnitTests.Protocol.Xml;

public sealed class S3CompleteXmlTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private Task<TimeStore.Primitives.Result<S3CompleteRequest>> Parse(string xml) =>
        S3XmlReader.ParseCompleteAsync(Encoding.UTF8.GetBytes(xml), _ct);

    [Fact]
    public async Task Parse_KeepsDocumentOrder_StripsQuotes_ReadsChecksums()
    {
        var result = await Parse("""
            <CompleteMultipartUpload xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Part><PartNumber>2</PartNumber><ETag>"bbb"</ETag></Part>
              <Part><ETag>aaa</ETag><PartNumber> 1 </PartNumber><ChecksumCRC32>AAAAAA==</ChecksumCRC32></Part>
            </CompleteMultipartUpload>
            """);

        Assert.Equal(
            [new S3CompletePart(2, "bbb", null, null), new S3CompletePart(1, "aaa", "CRC32", "AAAAAA==")],
            result.Value.Parts);
    }

    [Theory]
    [InlineData("<CompleteMultipartUpload/>")]
    [InlineData("<CompleteMultipartUpload></CompleteMultipartUpload>")]
    [InlineData("<CompleteMultipartUpload><Part><ETag>a</ETag></Part></CompleteMultipartUpload>")]
    [InlineData("<CompleteMultipartUpload><Part><PartNumber>1</PartNumber></Part></CompleteMultipartUpload>")]
    [InlineData("<CompleteMultipartUpload><Part><PartNumber>-1</PartNumber><ETag>a</ETag></Part></CompleteMultipartUpload>")]
    [InlineData("<CompleteMultipartUpload><Part><PartNumber>x</PartNumber><ETag>a</ETag></Part></CompleteMultipartUpload>")]
    [InlineData("<Other><Part><PartNumber>1</PartNumber><ETag>a</ETag></Part></Other>")]
    [InlineData("not xml")]
    public async Task Parse_RejectsMalformedDocuments(string xml) =>
        Assert.Equal(S3XmlErrors.Codes.MalformedXml, (await Parse(xml)).Error.Code);
}
