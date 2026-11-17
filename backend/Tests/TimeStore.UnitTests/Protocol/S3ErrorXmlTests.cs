using System.Xml.Linq;
using TimeStore.Protocol;

namespace TimeStore.UnitTests.Protocol;

public sealed class S3ErrorXmlTests
{
    [Fact]
    public async Task Write_ProducesS3ErrorDocument()
    {
        using var stream = new MemoryStream();

        await S3ErrorXml.WriteAsync(stream, S3ErrorCodes.NoSuchBucket, "missing", "/photos", "req-1");

        stream.Position = 0;
        var root = XDocument.Load(stream).Root!;
        Assert.Equal(S3Xml.Error, root.Name.LocalName);
        Assert.Equal(S3ErrorCodes.NoSuchBucket, root.Element(S3Xml.Code)!.Value);
        Assert.Equal("/photos", root.Element(S3Xml.Resource)!.Value);
        Assert.Equal("req-1", root.Element(S3Xml.RequestId)!.Value);
    }
}
