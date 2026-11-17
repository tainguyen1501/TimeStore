using TimeStore.Domain.Buckets;

namespace TimeStore.UnitTests.Buckets;

public sealed class BucketNamesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("my-bucket")]
    [InlineData("my.bucket.2026")]
    [InlineData("a23456789012345678901234567890123456789012345678901234567890123")]
    public void IsValid_WithValidName_ReturnsTrue(string name) => Assert.True(BucketNames.IsValid(name));

    [Theory]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("MyBucket")]
    [InlineData("-bucket")]
    [InlineData("bucket-")]
    [InlineData("my..bucket")]
    [InlineData("my_bucket")]
    [InlineData("192.168.5.4")]
    [InlineData("xn--bucket")]
    [InlineData("bucket-s3alias")]
    public void IsValid_WithInvalidName_ReturnsFalse(string name) => Assert.False(BucketNames.IsValid(name));
}
