using TimeStore.Primitives;
using TimeStore.Protocol.Routing;

namespace TimeStore.UnitTests.Protocol;

public sealed class S3RouterTests
{
    private const string Host = "localhost";
    private const string Domain = "s3.example.com";
    private readonly S3Router _router = new([Domain]);

    private S3Operation Route(string method, string target, string host = Host, string? copySource = null)
    {
        var result = _router.Route(method, host, target, copySource);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private AppError Fail(string method, string target, string host = Host)
    {
        var result = _router.Route(method, host, target, null);
        Assert.True(result.IsFailure, "Expected a routing error.");
        return result.Error;
    }

    // --- Service and bucket operations ---------------------------------------------------------------------------

    [Fact]
    public void GetRoot_IsListBuckets() => Assert.IsType<ListBuckets>(Route("GET", "/"));

    [Fact]
    public void PutRoot_IsMethodNotAllowed() =>
        Assert.Equal(S3RequestErrors.Codes.MethodNotAllowed, Fail("PUT", "/").Code);

    [Theory]
    [InlineData("PUT", "/photos", typeof(CreateBucket))]
    [InlineData("PUT", "/photos/", typeof(CreateBucket))]
    [InlineData("DELETE", "/photos", typeof(DeleteBucket))]
    [InlineData("HEAD", "/photos", typeof(HeadBucket))]
    [InlineData("GET", "/photos?location", typeof(GetBucketLocation))]
    [InlineData("GET", "/photos?versioning", typeof(GetBucketVersioning))]
    [InlineData("PUT", "/photos?versioning", typeof(PutBucketVersioning))]
    [InlineData("POST", "/photos?delete", typeof(DeleteObjects))]
    [InlineData("GET", "/photos", typeof(ListObjects))]
    [InlineData("GET", "/photos?list-type=2", typeof(ListObjectsV2))]
    [InlineData("GET", "/photos?versions", typeof(ListObjectVersions))]
    [InlineData("GET", "/photos?uploads", typeof(ListMultipartUploads))]
    public void BucketRequests_RouteToOperation(string method, string target, Type expected)
    {
        var operation = Route(method, target);

        Assert.IsType(expected, operation);
        Assert.Equal("photos", ((BucketOperation)operation).Bucket);
    }

    [Fact]
    public void ListObjectsV2_ParsesParameters()
    {
        var list = Assert.IsType<ListObjectsV2>(Route(
            "GET",
            "/photos?list-type=2&prefix=2026%2F11%2F&delimiter=%2F&continuation-token=abc&start-after=a+b"
            + "&max-keys=50&encoding-type=url&fetch-owner=true"));

        Assert.Equal("2026/11/", list.Prefix);
        Assert.Equal("/", list.Delimiter);
        Assert.Equal("abc", list.ContinuationToken);
        Assert.Equal("a b", list.StartAfter); // '+' is a space in query strings
        Assert.Equal(50, list.MaxKeys);
        Assert.True(list.EncodeUrl);
        Assert.True(list.FetchOwner);
    }

    [Theory]
    [InlineData("", 1000)]
    [InlineData("&max-keys=0", 0)]
    [InlineData("&max-keys=5000", 1000)]
    public void ListObjectsV2_MaxKeys_DefaultsAndCaps(string extra, int expected) =>
        Assert.Equal(expected, Assert.IsType<ListObjectsV2>(Route("GET", "/photos?list-type=2" + extra)).MaxKeys);

    [Fact]
    public void ListObjects_EmptyDelimiter_IsNull() =>
        Assert.Null(Assert.IsType<ListObjects>(Route("GET", "/photos?delimiter=")).Delimiter);

    [Theory]
    [InlineData("/photos?list-type=2&max-keys=-1")]
    [InlineData("/photos?list-type=2&max-keys=ten")]
    [InlineData("/photos?list-type=2&encoding-type=base64")]
    [InlineData("/photos?list-type=3")]
    public void InvalidListArguments_AreInvalidArgument(string target) =>
        Assert.Equal(S3RequestErrors.Codes.InvalidArgument, Fail("GET", target).Code);

    [Theory]
    [InlineData("PUT", "/photos?location")]
    [InlineData("DELETE", "/photos?uploads")]
    [InlineData("HEAD", "/photos?versions")]
    [InlineData("PATCH", "/photos")]
    [InlineData("GET", "/photos?delete")]
    public void WrongMethodForBucketResource_IsMethodNotAllowed(string method, string target) =>
        Assert.Equal(S3RequestErrors.Codes.MethodNotAllowed, Fail(method, target).Code);

    [Fact]
    public void PostBucketWithoutDelete_IsUnsupportedBrowserUpload() =>
        Assert.Equal(S3Features.PostObject, Assert.IsType<UnsupportedOperation>(Route("POST", "/photos")).Feature);

    // --- Object operations ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/photos/cat.jpg", typeof(GetObject))]
    [InlineData("HEAD", "/photos/cat.jpg", typeof(HeadObject))]
    [InlineData("PUT", "/photos/cat.jpg", typeof(PutObject))]
    [InlineData("DELETE", "/photos/cat.jpg", typeof(DeleteObject))]
    [InlineData("POST", "/photos/cat.jpg?uploads", typeof(CreateMultipartUpload))]
    [InlineData("PUT", "/photos/cat.jpg?partNumber=3&uploadId=u1", typeof(UploadPart))]
    [InlineData("POST", "/photos/cat.jpg?uploadId=u1", typeof(CompleteMultipartUpload))]
    [InlineData("DELETE", "/photos/cat.jpg?uploadId=u1", typeof(AbortMultipartUpload))]
    [InlineData("GET", "/photos/cat.jpg?uploadId=u1", typeof(ListParts))]
    public void ObjectRequests_RouteToOperation(string method, string target, Type expected)
    {
        var operation = Route(method, target);

        Assert.IsType(expected, operation);
        var objectOperation = (ObjectOperation)operation;
        Assert.Equal("photos", objectOperation.Bucket);
        Assert.Equal("cat.jpg", objectOperation.Key);
    }

    [Fact]
    public void PutWithCopySource_IsCopyObject() =>
        Assert.Equal("src/a.txt", Assert.IsType<CopyObject>(Route("PUT", "/photos/b.txt", copySource: "src/a.txt")).CopySource);

    [Fact]
    public void UploadPartWithCopySource_IsUploadPartCopy()
    {
        var copy = Assert.IsType<UploadPartCopy>(
            Route("PUT", "/photos/b.txt?partNumber=2&uploadId=u1", copySource: "src/a.txt"));

        Assert.Equal(2, copy.PartNumber);
        Assert.Equal("u1", copy.UploadId);
    }

    [Fact]
    public void GetObject_ParsesVersionAndPart()
    {
        var get = Assert.IsType<GetObject>(Route("GET", "/photos/cat.jpg?versionId=v1&partNumber=2"));

        Assert.Equal("v1", get.VersionId);
        Assert.Equal(2, get.PartNumber);
    }

    [Theory]
    [InlineData("PUT", "/photos/a?partNumber=0&uploadId=u1")]
    [InlineData("PUT", "/photos/a?partNumber=10001&uploadId=u1")]
    [InlineData("PUT", "/photos/a?partNumber=1")]
    [InlineData("PUT", "/photos/a?uploadId=u1")]
    [InlineData("GET", "/photos/a?uploadId=")]
    [InlineData("GET", "/photos/a?partNumber=x")]
    public void InvalidPartArguments_AreInvalidArgument(string method, string target) =>
        Assert.Equal(S3RequestErrors.Codes.InvalidArgument, Fail(method, target).Code);

    // --- Keys ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/photos/a%20b.txt", "a b.txt")]
    [InlineData("/photos/a+b.txt", "a+b.txt")] // '+' is literal in paths
    [InlineData("/photos/dir/sub/file", "dir/sub/file")]
    [InlineData("/photos/dir/", "dir/")] // trailing slash is part of the key
    [InlineData("/photos//double", "/double")]
    [InlineData("/photos/a/../b", "a/../b")] // keys never become paths in TimeStore
    [InlineData("/photos/%E1%BA%A3nh.jpg", "ảnh.jpg")]
    [InlineData("/photos/a%2Fb", "a/b")]
    public void Keys_AreDecodedExactly(string target, string expectedKey) =>
        Assert.Equal(expectedKey, ((ObjectOperation)Route("GET", target)).Key);

    [Fact]
    public void Key_At1024Bytes_IsAccepted() =>
        Assert.IsType<GetObject>(Route("GET", "/photos/" + new string('k', 1024)));

    [Fact]
    public void Key_Over1024Bytes_IsKeyTooLong() =>
        Assert.Equal(S3RequestErrors.Codes.KeyTooLong, Fail("GET", "/photos/" + new string('k', 1025)).Code);

    [Fact]
    public void Key_MultiByteOver1024Bytes_IsKeyTooLong() => // 342 × 3-byte character = 1026 bytes
        Assert.Equal(S3RequestErrors.Codes.KeyTooLong, Fail("GET", "/photos/" + string.Concat(Enumerable.Repeat("%E1%BA%A3", 342))).Code);

    [Theory]
    [InlineData("/photos/bad%zz")]
    [InlineData("/photos/truncated%2")]
    [InlineData("/photos/%FF%FE")] // not UTF-8
    [InlineData("/photos?prefix=%C3")] // truncated UTF-8 in the query
    [InlineData("photos/no-leading-slash")]
    public void MalformedTargets_AreInvalidUri(string target) =>
        Assert.Equal(S3RequestErrors.Codes.InvalidUri, Fail("GET", target).Code);

    // --- Sub-resources -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/photos?acl", "acl")]
    [InlineData("PUT", "/photos/cat.jpg?tagging", "tagging")]
    [InlineData("POST", "/photos/cat.jpg?restore", "restore")]
    public void UnsupportedSubresources_AreUnsupportedOperations(string method, string target, string feature) =>
        Assert.Equal(feature, Assert.IsType<UnsupportedOperation>(Route(method, target)).Feature);

    [Fact]
    public void PolicySubresources_RouteToPolicyOperations()
    {
        Assert.Equal("photos", Assert.IsType<GetBucketPolicy>(Route("GET", "/photos?policy")).Bucket);
        Assert.IsType<PutBucketPolicy>(Route("PUT", "/photos?policy"));
        Assert.IsType<DeleteBucketPolicy>(Route("DELETE", "/photos?policy"));
        Assert.IsType<GetBucketPolicyStatus>(Route("GET", "/photos?policyStatus"));
    }

    [Fact]
    public void TwoOperationSubresources_AreAmbiguous() =>
        Assert.Equal(S3RequestErrors.Codes.InvalidRequest, Fail("GET", "/photos?versions&uploads").Code);

    // --- Virtual host --------------------------------------------------------------------------------------------

    [Fact]
    public void VirtualHost_BucketFromHost() =>
        Assert.Equal("cat.jpg", Assert.IsType<GetObject>(Route("GET", "/cat.jpg", host: "photos." + Domain)).Key);

    [Fact]
    public void VirtualHost_RootIsBucket() =>
        Assert.Equal("my.photos", Assert.IsType<ListObjects>(Route("GET", "/", host: "my.photos." + Domain)).Bucket);

    [Fact]
    public void VirtualHost_IsCaseInsensitiveOnTheDomain() =>
        Assert.Equal("photos", Assert.IsType<HeadBucket>(Route("HEAD", "/", host: "photos.S3.Example.COM")).Bucket);

    [Theory]
    [InlineData(Domain)] // the domain itself: path style
    [InlineData("127.0.0.1")]
    [InlineData("other.example.org")]
    public void OtherHosts_UsePathStyle(string host) =>
        Assert.Equal("photos", Assert.IsType<HeadBucket>(Route("HEAD", "/photos", host: host)).Bucket);
}
