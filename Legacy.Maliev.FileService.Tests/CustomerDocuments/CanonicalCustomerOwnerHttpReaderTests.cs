using System.Net;
using System.Text;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Controlled owner HTTP responses prove transport parsing only, never current membership or deployed-owner acceptance.
public sealed class CanonicalCustomerOwnerHttpReaderTests
{
    [Fact]
    public async Task ExactPositivePascalIdentityAndServerCredentialProveExistenceOnly()
    {
        using var factory = Fixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/customers/23", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-server-credential", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("{\"Id\":23,\"FirstName\":\"Synthetic\",\"CompanyId\":999}"));
        });
        var result = await Reader(factory).ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Allowed, result.Outcome);
        Assert.Equal(new CanonicalDocumentCustomer(23), result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingCredentialIsUnknownProofAndMakesNoOwnerRequest(string? accessToken)
    {
        var calls = 0;
        using var factory = Fixture((_, _) => { calls++; return Task.FromResult(Json("{\"Id\":23}")); });
        var result = await new CustomerDocumentCanonicalCustomerHttpReader(factory, new Credential(accessToken)).ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AbsentCredentialBoundaryNeverMakesOwnerRequest()
    {
        using var factory = Fixture((_, _) => throw new InvalidOperationException("No owner request is permitted"));
        var result = await new CustomerDocumentCanonicalCustomerHttpReader(factory).ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(401, DocumentAuthorityOutcome.Denied)]
    [InlineData(403, DocumentAuthorityOutcome.Denied)]
    [InlineData(404, DocumentAuthorityOutcome.Denied)]
    [InlineData(302, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(500, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(503, DocumentAuthorityOutcome.Unavailable)]
    public async Task OwnerStatusRefusesWithoutSubstituteEvidence(int status, DocumentAuthorityOutcome expected)
    {
        using var factory = Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var result = await Reader(factory).ReadAsync(23, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("{\"Id\":24}")]
    [InlineData("{\"Id\":0}")]
    [InlineData("{\"Id\":-1}")]
    [InlineData("{\"Id\":2147483648}")]
    [InlineData("{\"Id\":23.5}")]
    [InlineData("{\"Id\":\"23\"}")]
    [InlineData("{\"Id\":null}")]
    [InlineData("{\"id\":23}")]
    [InlineData("{\"CustomerId\":23}")]
    [InlineData("{\"Id\":23,\"Id\":24}")]
    [InlineData("{\"Id\":23,\"id\":24}")]
    [InlineData("{\"Id\":23,\"Extra\":{\"X\":1,\"X\":2}}")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public async Task MalformedMismatchedOrAmbiguousIdentityIsUnavailable(string body)
    {
        using var factory = Fixture((_, _) => Task.FromResult(Json(body)));
        var result = await Reader(factory).ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("size")]
    [InlineData("redirect")]
    [InlineData("origin")]
    public async Task UnsafeTransportEvidenceIsUnavailable(string fault)
    {
        using var factory = Fixture((request, _) =>
        {
            var response = Json(fault == "size" ? "{\"Id\":23,\"Padding\":\"" + new string('x', 65536) + "\"}" : "{\"Id\":23}");
            if (fault == "media") response.Content.Headers.ContentType = new("text/plain");
            if (fault == "redirect") response.Headers.Location = new("https://other.example.invalid/");
            if (fault == "origin") response.RequestMessage = new(HttpMethod.Get, "https://other.example.invalid/customers/23");
            else response.RequestMessage = request;
            return Task.FromResult(response);
        });
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await Reader(factory).ReadAsync(23, default)).Outcome);
    }

    [Theory]
    [InlineData(65536, DocumentAuthorityOutcome.Allowed)]
    [InlineData(65537, DocumentAuthorityOutcome.Unavailable)]
    public async Task UnadvertisedBodySizeEnforcesInclusive64KiBBoundary(int bytes, DocumentAuthorityOutcome expected)
    {
        const string prefix = "{\"Id\":23,\"Padding\":\"";
        const string suffix = "\"}";
        var payload = Encoding.UTF8.GetBytes(prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix);
        using var factory = Fixture((_, _) =>
        {
            var content = new UnknownLengthContent(payload);
            content.Headers.ContentType = new("application/json");
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        Assert.Equal(expected, (await Reader(factory).ReadAsync(23, default)).Outcome);
    }

    [Fact]
    public async Task InjectedTimeoutReturnsUnavailableWhileCallerCancellationPropagates()
    {
        using var factory = Fixture(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{\"Id\":23}");
        });
        var reader = new CustomerDocumentCanonicalCustomerHttpReader(factory, new Credential("synthetic-server-credential"), TimeSpan.FromMilliseconds(20));
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await reader.ReadAsync(23, default)).Outcome);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(23, caller.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveRequestedIdentityNeverMakesOwnerRequest(int customerId)
    {
        using var factory = Fixture((_, _) => throw new InvalidOperationException("No owner request is permitted"));
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await Reader(factory).ReadAsync(customerId, default)).Outcome);
    }

    [Theory]
    [InlineData("http://crm.example.invalid/")]
    [InlineData("https://user:password@crm.example.invalid/")]
    [InlineData("https://crm.example.invalid/base/")]
    [InlineData("https://crm.example.invalid/?query=1")]
    [InlineData("https://crm.example.invalid/#fragment")]
    public void ProductionOriginRequiresExplicitHttpsRoot(string origin) =>
        Assert.Throws<ArgumentException>(() => new CustomerDocumentCanonicalCustomerHttpClientFactory(new Uri(origin)));

    private static CustomerDocumentCanonicalCustomerHttpReader Reader(CustomerDocumentCanonicalCustomerHttpClientFactory factory) =>
        new(factory, new Credential("synthetic-server-credential"));
    private static CustomerDocumentCanonicalCustomerHttpClientFactory Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        CustomerDocumentCanonicalCustomerHttpClientFactory.CreateForIsolatedLoopbackTests(new("http://127.0.0.1:32191/"), new Handler(respond));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Credential(string? value) : ICustomerDocumentOwnerCredential
    {
        public Task<string?> GetAccessTokenAsync(Uri ownerOrigin, CancellationToken token) => Task.FromResult(value);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(payload).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
    }
}
