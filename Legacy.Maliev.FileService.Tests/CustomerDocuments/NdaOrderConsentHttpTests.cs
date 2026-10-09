using System.Net;
using System.Text;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class NdaOrderConsentHttpTests
{
    [Theory]
    [InlineData("{\"Id\":81,\"CustomerId\":23,\"AllowSocialMedia\":false}", DocumentAuthorityOutcome.Allowed, false)]
    [InlineData("{\"Id\":81,\"CustomerId\":23,\"AllowSocialMedia\":true}", DocumentAuthorityOutcome.Allowed, true)]
    [InlineData("{\"Id\":81,\"CustomerId\":24,\"AllowSocialMedia\":true}", DocumentAuthorityOutcome.Denied, false)]
    [InlineData("{\"Id\":81,\"CustomerId\":23}", DocumentAuthorityOutcome.Unavailable, false)]
    [InlineData("{\"Id\":81,\"CustomerId\":23,\"AllowSocialMedia\":false,\"AllowSocialMedia\":true}", DocumentAuthorityOutcome.Unavailable, false)]
    public async Task PinnedOrderConsentRequiresExactScopeAndOneExplicitBoolean(string payload, DocumentAuthorityOutcome outcome, bool consent)
    {
        using var factory = CustomerDocumentOwnerHttpClientFactory.CreateForIsolatedLoopbackTests(new("http://127.0.0.1:12345/"), new ConsentHandler(payload));
        var reader = new NdaOrderConsentHttpReader(factory, new ConsentCredential());
        var result = await reader.ReadAsync(81, 23, default);
        Assert.Equal(outcome, result.Outcome);
        if (outcome == DocumentAuthorityOutcome.Allowed) Assert.Equal(consent, result.Value);
    }
    [Fact]
    public async Task MissingCurrentServerCredentialIsUnavailable()
    {
        using var factory = CustomerDocumentOwnerHttpClientFactory.CreateForIsolatedLoopbackTests(new("http://127.0.0.1:12345/"), new ConsentHandler("{}"));
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await new NdaOrderConsentHttpReader(factory).ReadAsync(81, 23, default)).Outcome);
    }
    private sealed class ConsentCredential : ICustomerDocumentOwnerCredential
    {
        public Task<string?> GetAccessTokenAsync(Uri origin, CancellationToken token) => Task.FromResult<string?>("synthetic-server-token");
    }
    private sealed class ConsentHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StringContent(payload, Encoding.UTF8, "application/json") });
    }
}
