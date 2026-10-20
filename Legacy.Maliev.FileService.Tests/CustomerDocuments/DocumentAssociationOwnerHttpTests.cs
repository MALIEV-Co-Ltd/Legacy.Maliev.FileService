using System.Net;
using System.Security.Claims;
using System.Text;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Controlled HTTP payloads exercise the actual adapter; they are not production owner joins or canonical ID receipts.
public sealed class DocumentAssociationOwnerHttpTests
{
    [Theory]
    [InlineData("{\"Id\":31,\"CustomerId\":7}", DocumentAuthorityOutcome.Allowed)]
    [InlineData("{\"Id\":32,\"CustomerId\":7}", DocumentAuthorityOutcome.Denied)]
    [InlineData("{\"Id\":31}", DocumentAuthorityOutcome.Unavailable)]
    [InlineData("{\"Id\":31,\"CustomerId\":null}", DocumentAuthorityOutcome.Unavailable)]
    [InlineData("{\"Id\":31,\"CustomerId\":7,\"CustomerId\":8}", DocumentAuthorityOutcome.Unavailable)]
    [InlineData("{\"Id\":31,\"CustomerId\":7,\"customerid\":8}", DocumentAuthorityOutcome.Unavailable)]
    [InlineData("{\"id\":31,\"customerId\":7}", DocumentAuthorityOutcome.Unavailable)]
    [InlineData("{\"Id\":31,\"CustomerId\":7,\"Extra\":{\"X\":1,\"X\":2}}", DocumentAuthorityOutcome.Unavailable)]
    [InlineData("{broken", DocumentAuthorityOutcome.Unavailable)]
    public async Task Strict_order_payload(string body, DocumentAuthorityOutcome expected)
    {
        using var factory = FixtureFactory((request, token) => Response(body));
        var reads = new CustomerDocumentOwnerHttpReads(factory, credential: new ControlledCredential());
        Assert.Equal(expected, (await reads.ReadOrderAsync(31, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(401, DocumentAuthorityOutcome.Denied)]
    [InlineData(403, DocumentAuthorityOutcome.Denied)]
    [InlineData(404, DocumentAuthorityOutcome.Denied)]
    [InlineData(302, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(503, DocumentAuthorityOutcome.Unavailable)]
    public async Task Owner_status_is_fail_closed(int status, DocumentAuthorityOutcome expected)
    {
        using var factory = FixtureFactory((request, token) => new((HttpStatusCode)status));
        var reads = new CustomerDocumentOwnerHttpReads(factory, credential: new ControlledCredential());
        Assert.Equal(expected, (await reads.ReadOrderAsync(31, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("{\"Quotation\":{\"Id\":9,\"CustomerId\":7,\"Accepted\":false}}", DocumentAuthorityOutcome.Allowed)]
    [InlineData("{\"Quotation\":{\"Id\":9,\"CustomerId\":8}}", DocumentAuthorityOutcome.Denied)]
    [InlineData("{\"Quotation\":{\"Id\":10,\"CustomerId\":7}}", DocumentAuthorityOutcome.Denied)]
    [InlineData("{\"Id\":9,\"CustomerId\":7}", DocumentAuthorityOutcome.Unavailable)]
    public async Task Scoped_quotation_identity_does_not_infer_acceptance(string body, DocumentAuthorityOutcome expected)
    {
        using var factory = FixtureFactory((request, token) =>
        {
            Assert.Equal("/Quotations/9?customerId=7", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Response(body);
        });
        var reads = new CustomerDocumentOwnerHttpReads(factory, credential: new ControlledCredential());
        Assert.Equal(expected, (await reads.ReadQuotationAsync(9, 7, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("[{\"Id\":1,\"QuotationId\":9,\"OrderId\":31}]", DocumentAuthorityOutcome.Allowed)]
    [InlineData("[]", DocumentAuthorityOutcome.Denied)]
    [InlineData("[{\"Id\":1,\"QuotationId\":10,\"OrderId\":31}]", DocumentAuthorityOutcome.Denied)]
    [InlineData("[{\"Id\":1,\"QuotationId\":9,\"OrderId\":31},{\"Id\":2,\"QuotationId\":9,\"OrderId\":31}]", DocumentAuthorityOutcome.Denied)]
    public async Task Exact_quotation_links(string body, DocumentAuthorityOutcome expected)
    {
        using var factory = FixtureFactory((request, token) => Response(body));
        var reads = new CustomerDocumentOwnerHttpReads(factory, credential: new ControlledCredential());
        Assert.Equal(expected, (await reads.ReadQuotationOrdersAsync(9, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Actual_adapter_requires_customer_and_order_owner_and_exact_link()
    {
        using var factory = FixtureFactory((request, token) => request.RequestUri!.AbsolutePath switch
        {
            "/orders/31" => Response("{\"Id\":31,\"CustomerId\":7}"),
            "/Quotations/9" => Response("{\"Quotation\":{\"Id\":9,\"CustomerId\":7}}"),
            "/quotations/9/orders" => Response("[{\"Id\":1,\"QuotationId\":9,\"OrderId\":31}]"),
            _ => new(HttpStatusCode.NotFound)
        });
        var client = new CustomerDocumentAssociationClient(new CustomerDocumentOwnerHttpReads(factory,
            new ControlledCustomerReader(), new ControlledCredential()));
        Assert.Equal(DocumentAuthorityOutcome.Allowed, await client.ValidateAsync(new(new ClaimsPrincipal()), 7,
            [new() { Kind = DocumentResourceKind.Order, ResourceId = 31, CustomerId = 7 },
             new() { Kind = DocumentResourceKind.Quotation, ResourceId = 9, CustomerId = 7 }], CancellationToken.None));
    }

    [Fact]
    public async Task Pending_CRM_and_credential_contracts_do_not_send_or_allow()
    {
        using var factory = FixtureFactory((request, token) => throw new InvalidOperationException("Must not send."));
        var reads = new CustomerDocumentOwnerHttpReads(factory);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await reads.ReadCustomerAsync(7, CancellationToken.None)).Outcome);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await reads.ReadOrderAsync(31, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Oversized_body_and_redirected_request_are_unavailable()
    {
        using var oversized = FixtureFactory((request, token) => Response(new string(' ', 65537)));
        var reads = new CustomerDocumentOwnerHttpReads(oversized, credential: new ControlledCredential());
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await reads.ReadOrderAsync(31, CancellationToken.None)).Outcome);
        using var redirected = FixtureFactory((request, token) =>
        {
            var response = Response("{\"Id\":31,\"CustomerId\":7}");
            response.RequestMessage = new(HttpMethod.Get, "https://untrusted.invalid/orders/31");
            return response;
        });
        reads = new CustomerDocumentOwnerHttpReads(redirected, credential: new ControlledCredential());
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await reads.ReadOrderAsync(31, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var factory = FixtureFactory((request, token) => throw new OperationCanceledException(token));
        var reads = new CustomerDocumentOwnerHttpReads(factory, credential: new ControlledCredential());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reads.ReadOrderAsync(31, cancellation.Token));
    }

    [Theory]
    [InlineData("http://owner.invalid/")]
    [InlineData("https://owner.invalid/subpath/")]
    [InlineData("https://user:password@owner.invalid/")]
    [InlineData("https://owner.invalid/?x=1")]
    public void Production_factory_refuses_unsafe_origins(string origin)
    {
        Assert.Throws<ArgumentException>(() => new CustomerDocumentOwnerHttpClientFactory(new(origin), new("https://quotation.invalid/")));
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static CustomerDocumentOwnerHttpClientFactory FixtureFactory(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) =>
        CustomerDocumentOwnerHttpClientFactory.CreateForIsolatedLoopbackTests(new("http://127.0.0.1:45877/"), new ControlledHandler(response));
    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request, cancellationToken));
    }
    private sealed class ControlledCredential : ICustomerDocumentOwnerCredential
    {
        public Task<string?> GetAccessTokenAsync(Uri ownerOrigin, CancellationToken token) => Task.FromResult<string?>("isolated-synthetic-test-credential");
    }
    private sealed class ControlledCustomerReader : ICanonicalDocumentCustomerReader
    {
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentCustomer>(DocumentAuthorityOutcome.Allowed, new(id)));
    }
}
