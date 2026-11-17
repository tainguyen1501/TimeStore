using TimeStore.Protocol.Routing;

namespace TimeStore.UnitTests.Protocol;

public sealed class CopySourceRangeTests
{
    [Theory]
    [InlineData("bytes=0-0", 0, 0)]
    [InlineData("bytes=0-5242879", 0, 5242879)]
    [InlineData("bytes=10-20", 10, 20)]
    public void ParsesTheOneValidForm(string text, long first, long last)
    {
        Assert.True(S3CopySourceRange.TryParse(text, out var range));
        Assert.Equal((first, last, last - first + 1), (range.First, range.Last, range.Length));
    }

    [Theory]
    [InlineData("0-2")] // no unit
    [InlineData("bytes=0")] // no end
    [InlineData("bytes=hello-world")]
    [InlineData("bytes=0-bar")]
    [InlineData("bytes=hello-")]
    [InlineData("bytes=0-2,3-5")] // several ranges
    [InlineData("bytes=-5")] // suffix range
    [InlineData("bytes=5-2")] // end before start
    [InlineData("bytes= 0-2")]
    [InlineData("bytes=+0-2")]
    [InlineData("")]
    public void RefusesEveryOtherForm(string text) => Assert.False(S3CopySourceRange.TryParse(text, out _));
}
