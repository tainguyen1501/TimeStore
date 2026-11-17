using System.Text;
using TimeStore.Server.S3;

namespace TimeStore.Server.Tests.Http;

/// <summary>
/// Non-ASCII user metadata on responses (found by ceph s3-tests: it used to fail the response with a 500). The AWS SDK
/// for .NET cannot send such a header at all (HttpClient allows ASCII only), so the end-to-end proof is the s3-tests
/// case <c>test_object_set_get_unicode_metadata</c> (botocore); these pin the encoding decision.
/// </summary>
public sealed class MetadataHeaderTests
{
    [Theory]
    [InlineData("plain ascii")]
    [InlineData("Hello Worldé")] // Latin-1: written as is, one byte per character
    public void Values_Latin1CanCarry_AreWrittenUnchanged(string value) => Assert.Equal(value, S3MetadataHeaders.Value(value));

    [Fact]
    public void Values_BeyondLatin1_AreRfc2047Encoded_WithoutLoss()
    {
        const string value = "ảnh có dấu";

        var written = S3MetadataHeaders.Value(value);

        Assert.StartsWith("=?UTF-8?B?", written);
        Assert.Equal(value, Encoding.UTF8.GetString(Convert.FromBase64String(written["=?UTF-8?B?".Length..^2])));
        Assert.All(written, c => Assert.True(c < 0x80));
    }

    [Theory]
    [InlineData("x-amz-meta-note", true)]
    [InlineData("X-Amz-Meta-Note", true)]
    [InlineData("content-type", false)]
    [InlineData("x-amz-checksum-crc32", false)]
    public void OnlyUserMetadata_GetsLatin1(string header, bool latin1) =>
        Assert.Equal(latin1 ? Encoding.Latin1 : null, S3MetadataHeaders.Encoding(header));
}
