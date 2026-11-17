using System.Text;
using TimeStore.Protocol.Xml;

namespace TimeStore.UnitTests.Protocol.Xml;

public sealed class S3DeleteXmlTests
{
    private const string Xmlns = "xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private async Task<S3DeleteRequest> Parse(string xml)
    {
        var result = await S3XmlReader.ParseDeleteAsync(Encoding.UTF8.GetBytes(xml), _ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private async Task<string> ParseError(string xml) =>
        (await S3XmlReader.ParseDeleteAsync(Encoding.UTF8.GetBytes(xml), _ct)).Error.Code;

    [Fact]
    public async Task Parse_ReadsQuietKeysAndVersions_KeepingSpaces()
    {
        var request = await Parse(
            $"<Delete {Xmlns}><Quiet>true</Quiet><Object><Key> a b.txt </Key></Object>"
            + "<Object><Key>x&amp;y</Key><VersionId>null</VersionId></Object></Delete>");

        Assert.True(request.Quiet);
        Assert.Equal(new[] { new S3DeleteEntry(" a b.txt ", null), new S3DeleteEntry("x&y", "null") }, request.Objects);
    }

    [Fact]
    public async Task Parse_WithoutQuiet_IsVerbose() =>
        Assert.False((await Parse("<Delete><Object><Key>a</Key></Object></Delete>")).Quiet);

    [Theory]
    [InlineData("<Delete></Delete>")]
    [InlineData("<Delete><Object></Object></Delete>")]
    [InlineData("<Delete><Object><Key></Key></Object></Delete>")]
    [InlineData("<Other><Object><Key>a</Key></Object></Other>")]
    [InlineData("<Delete><Object><Key>a</Key></Object>")]
    public async Task Parse_Invalid_IsMalformedXml(string xml) =>
        Assert.Equal(S3XmlErrors.Codes.MalformedXml, await ParseError(xml));

    [Fact]
    public async Task Parse_MoreThan1000Keys_IsMalformedXml()
    {
        var objects = string.Concat(Enumerable.Range(0, 1_001).Select(i => $"<Object><Key>k{i}</Key></Object>"));

        Assert.Equal(S3XmlErrors.Codes.MalformedXml, await ParseError($"<Delete>{objects}</Delete>"));
    }

    [Fact]
    public async Task Parse_Exactly1000Keys_IsAccepted()
    {
        var objects = string.Concat(Enumerable.Range(0, 1_000).Select(i => $"<Object><Key>k{i}</Key></Object>"));

        Assert.Equal(1_000, (await Parse($"<Delete>{objects}</Delete>")).Objects.Count);
    }

    [Fact]
    public async Task ReadBody_AboveTheLimit_IsMalformedXml()
    {
        var result = await S3XmlReader.ReadBodyAsync(new MemoryStream(new byte[2_000]), 1_000, _ct);

        Assert.Equal(S3XmlErrors.Codes.MalformedXml, result.Error.Code);
    }

    [Fact]
    public async Task ReadBody_GrowsBeyondTheInitialBuffer()
    {
        var bytes = Enumerable.Repeat((byte)'x', 200_000).ToArray();

        var result = await S3XmlReader.ReadBodyAsync(new MemoryStream(bytes), S3XmlReader.MaxDeleteDocumentBytes, _ct);

        Assert.Equal(bytes, result.Value);
    }

    [Theory]
    [InlineData(false, "<Deleted><Key>a</Key></Deleted><Error><Key>b</Key><Code>KeyTooLongError</Code><Message>m</Message></Error>")]
    [InlineData(true, "<Error><Key>b</Key><Code>KeyTooLongError</Code><Message>m</Message></Error>")]
    public async Task DeleteResult_ListsDeletedUnlessQuiet_AndAlwaysErrors(bool quiet, string body)
    {
        using var stream = new MemoryStream();

        await S3XmlWriter.DeleteResultAsync(stream, [new S3Deleted("a", null, false, null)], [new S3DeleteError("b", "KeyTooLongError", "m")], quiet);

        Assert.EndsWith($"<DeleteResult {Xmlns}>{body}</DeleteResult>", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("  \t ")]
    [InlineData(" leading and trailing ")]
    public async Task Parse_KeepsKeysMadeOfWhitespace(string key)
    {
        var xml = $"""
            <Delete xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Object>
                <Key>{key}</Key>
                <VersionId>null</VersionId>
              </Object>
              <Quiet>true</Quiet>
            </Delete>
            """;

        var result = await S3XmlReader.ParseDeleteAsync(Encoding.UTF8.GetBytes(xml), TestContext.Current.CancellationToken);

        Assert.Equal(key, Assert.Single(result.Value.Objects).Key);
        Assert.True(result.Value.Quiet);
    }
}
