using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace TimeStore.UnitTests.Fakes;

/// <summary>
/// A real <see cref="AmazonS3Client"/> whose HTTP layer is replaced by an in-memory handler: requests are signed by
/// the SDK exactly as on the wire, captured (headers and body), and answered with an empty 200. No network.
/// </summary>
public sealed class AwsSdkCapture : IDisposable
{
    public const string AccessKey = "timestore-test";
    public const string SecretKey = "timestore-test-secret";

    private readonly CapturingHandler _handler = new();

    public AwsSdkCapture() =>
        Client = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
        {
            ServiceURL = "http://localhost:9000",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            HttpClientFactory = new CapturingFactory(_handler),
        });

    public AmazonS3Client Client { get; }

    public IReadOnlyList<CapturedRequest> Requests => _handler.Requests;

    /// <summary>Runs an SDK call; the canned response may not satisfy the SDK's parser, which is irrelevant here.</summary>
    public async Task<CapturedRequest> CaptureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (AmazonServiceException)
        {
        }

        return Assert.Single(_handler.Requests);
    }

    public void Dispose() => Client.Dispose();

    public sealed record CapturedRequest(FakeSigV4Request Request, byte[] Body);

    private sealed class CapturingFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var headers = new Dictionary<string, string> { ["host"] = request.Headers.Host ?? uri.Authority };
            foreach (var (name, values) in request.Headers.NonValidated)
                headers[name.ToLowerInvariant()] = values.ToString();

            byte[] body = [];
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsByteArrayAsync(ct);
                foreach (var (name, values) in request.Content.Headers.NonValidated)
                    headers[name.ToLowerInvariant()] = values.ToString();
            }

            Requests.Add(new CapturedRequest(new FakeSigV4Request(request.Method.Method, uri.PathAndQuery, headers), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }
}
