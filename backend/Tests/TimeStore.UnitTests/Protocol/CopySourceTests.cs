using TimeStore.Protocol.Routing;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Protocol;

public sealed class CopySourceTests
{
    [Theory]
    [InlineData("bucket/key.txt", "bucket", "key.txt", null)]
    [InlineData("/bucket/folder/key.txt", "bucket", "folder/key.txt", null)]
    [InlineData("bucket/folder%2Fa%20b.txt", "bucket", "folder/a b.txt", null)]
    [InlineData("bucket%2Ffolder%2Fa%20b.txt", "bucket", "folder/a b.txt", null)] // the whole value encoded
    [InlineData("bucket/a+b.txt", "bucket", "a+b.txt", null)] // '+' is a plus here, not a space
    [InlineData("bucket/key.txt?versionId=null", "bucket", "key.txt", "null")]
    [InlineData("bucket/what%3F.txt?versionId=v1", "bucket", "what?.txt", "v1")]
    public void Parse_AcceptsEveryEncodingClientsUse(string raw, string bucket, string key, string? version) =>
        Assert.Equal(new S3CopySource(bucket, key, version), S3CopySource.Parse(raw).Value);

    [Theory]
    [InlineData("")]
    [InlineData("bucket")]
    [InlineData("bucket/")]
    [InlineData("/key-only")]
    [InlineData("bucket/%zz")]
    public void Parse_RejectsSourcesWithoutBucketAndKey(string raw) =>
        Assert.Equal(S3RequestErrors.Codes.InvalidArgument, S3CopySource.Parse(raw).Error.Code);

    [Fact]
    public async Task Parse_ReadsWhatTheAwsSdkSends()
    {
        using var capture = new AwsSdkCapture();
        const string key = "folder/ảnh có dấu & space.txt";
        var request = await capture.CaptureAsync(() => capture.Client.CopyObjectAsync(
            "src-bucket", key, "dst-bucket", "target.txt", TestContext.Current.CancellationToken));

        Assert.Equal(new S3CopySource("src-bucket", key, null), S3CopySource.Parse(request.Request.GetHeader("x-amz-copy-source")!).Value);
    }
}
