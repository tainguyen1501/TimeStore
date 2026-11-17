using TimeStore.Protocol.Http;
using TimeStore.Protocol.Routing;

namespace TimeStore.UnitTests.Protocol;

public sealed class HttpSemanticsTests
{
    // --- Range ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bytes=0-9", 100, 0, 10)]
    [InlineData("bytes=90-", 100, 90, 10)]
    [InlineData("bytes=-10", 100, 90, 10)]
    [InlineData("bytes=50-500", 100, 50, 50)] // end past the size is clamped
    [InlineData("bytes=-500", 100, 0, 100)] // suffix longer than the object: whole object as a range
    [InlineData("bytes=99-99", 100, 99, 1)]
    [InlineData("BYTES= 1 - 2 ", 100, 1, 2)]
    public void Range_Satisfiable_IsPartial(string header, long size, long start, long length)
    {
        var range = S3Range.Parse(header, size);

        Assert.Equal(RangeKind.Partial, range.Kind);
        Assert.Equal(new ByteRange(start, length), range.Range);
    }

    [Theory]
    [InlineData("bytes=100-", 100)]
    [InlineData("bytes=200-300", 100)]
    [InlineData("bytes=-0", 100)]
    [InlineData("bytes=0-0", 0)]
    [InlineData("bytes=-5", 0)]
    public void Range_OutsideTheObject_IsUnsatisfiable(string header, long size) =>
        Assert.Equal(RangeKind.Unsatisfiable, S3Range.Parse(header, size).Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-9")]
    [InlineData("bytes=0-9,20-29")] // several ranges: S3 sends the whole object
    [InlineData("bytes=9-0")] // end before start: invalid, ignored
    [InlineData("bytes=a-b")]
    [InlineData("bytes=5")]
    public void Range_MissingOrIgnored_IsWhole(string? header) =>
        Assert.Equal(RangeKind.Whole, S3Range.Parse(header, 100).Kind);

    [Fact]
    public void ByteRange_FormatsContentRange() =>
        Assert.Equal("bytes 90-99/100", new ByteRange(90, 10).ContentRange(100));

    // --- Preconditions -------------------------------------------------------------------------------------------

    private const string ETag = "abc123";
    private static readonly DateTime Modified = new(2026, 11, 16, 9, 30, 15, 500, DateTimeKind.Utc);
    private const string Before = "Mon, 16 Nov 2026 09:30:14 GMT";
    private const string Same = "Mon, 16 Nov 2026 09:30:15 GMT"; // milliseconds ignored
    private const string After = "Mon, 16 Nov 2026 09:30:16 GMT";

    private static PreconditionOutcome Evaluate(
        string? ifMatch = null, string? ifNoneMatch = null, string? ifModifiedSince = null, string? ifUnmodifiedSince = null) =>
        S3Preconditions.Evaluate(ifMatch, ifNoneMatch, ifModifiedSince, ifUnmodifiedSince, ETag, Modified);

    [Fact]
    public void NoConditions_Proceed() => Assert.Equal(PreconditionOutcome.Proceed, Evaluate());

    [Theory]
    [InlineData("\"abc123\"")]
    [InlineData("abc123")]
    [InlineData("W/\"abc123\"")]
    [InlineData("\"other\", \"abc123\"")]
    [InlineData("*")]
    public void IfMatch_Matching_Proceeds(string ifMatch) => Assert.Equal(PreconditionOutcome.Proceed, Evaluate(ifMatch));

    [Fact]
    public void IfMatch_Different_Fails() => Assert.Equal(PreconditionOutcome.Failed, Evaluate("\"other\""));

    [Fact]
    public void IfMatch_TrueWins_OverIfUnmodifiedSinceFalse() => // Amazon S3 documented combination
        Assert.Equal(PreconditionOutcome.Proceed, Evaluate(ifMatch: "\"abc123\"", ifUnmodifiedSince: Before));

    [Theory]
    [InlineData(Before, PreconditionOutcome.Failed)]
    [InlineData(Same, PreconditionOutcome.Proceed)]
    [InlineData(After, PreconditionOutcome.Proceed)]
    [InlineData("not a date", PreconditionOutcome.Proceed)]
    public void IfUnmodifiedSince(string date, PreconditionOutcome expected) =>
        Assert.Equal(expected, Evaluate(ifUnmodifiedSince: date));

    [Theory]
    [InlineData("\"abc123\"", PreconditionOutcome.NotModified)]
    [InlineData("*", PreconditionOutcome.NotModified)]
    [InlineData("\"other\"", PreconditionOutcome.Proceed)]
    public void IfNoneMatch(string ifNoneMatch, PreconditionOutcome expected) =>
        Assert.Equal(expected, Evaluate(ifNoneMatch: ifNoneMatch));

    [Theory]
    [InlineData(Before, PreconditionOutcome.Proceed)]
    [InlineData(Same, PreconditionOutcome.NotModified)]
    [InlineData(After, PreconditionOutcome.NotModified)]
    public void IfModifiedSince(string date, PreconditionOutcome expected) =>
        Assert.Equal(expected, Evaluate(ifModifiedSince: date));

    [Fact]
    public void IfNoneMatch_TakesPrecedence_OverIfModifiedSince() =>
        Assert.Equal(PreconditionOutcome.Proceed, Evaluate(ifNoneMatch: "\"other\"", ifModifiedSince: After));

    // --- Response overrides --------------------------------------------------------------------------------------

    [Fact]
    public void GetObject_ResponseOverrides_AreParsed()
    {
        var get = Assert.IsType<GetObject>(new S3Router([]).Route(
            "GET", "localhost", "/photos/a.bin?response-content-type=text%2Fplain&response-content-disposition=attachment",
            null).Value);

        Assert.Contains(new KeyValuePair<string, string>("Content-Type", "text/plain"), get.ResponseHeaders);
        Assert.Contains(new KeyValuePair<string, string>("Content-Disposition", "attachment"), get.ResponseHeaders);
    }
}
