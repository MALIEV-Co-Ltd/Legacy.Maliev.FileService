using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class DocumentAssociationOwnerCredentialTests
{
    [Fact]
    public async Task Missing_own_service_auth_binding_cannot_request_credentials_even_for_approved_origin()
    {
        var provider = new ControlledProvider();
        var bridge = new CustomerDocumentOwnerCredential(provider, [new("https://order.invalid/")]);
        Assert.Null(await bridge.GetAccessTokenAsync(new("https://unapproved.invalid/"), CancellationToken.None));
        Assert.Equal(0, provider.Reads);
        Assert.Null(await bridge.GetAccessTokenAsync(new("https://order.invalid/"), CancellationToken.None));
        Assert.Equal(0, provider.Reads);
    }
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://order.invalid/path/")]
    [InlineData("https://order.invalid/?x=1")]
    public void Credential_bridge_requires_production_https_origins(string origin)
    {
        Assert.Throws<ArgumentException>(() => new CustomerDocumentOwnerCredential(new ControlledProvider(), [new(origin)]));
    }
    private sealed class ControlledProvider : ILegacyServiceAccessTokenProvider
    {
        public int Reads { get; private set; }
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult<string?>("isolated-credential");
        }
        public void Invalidate(string token) { }
    }
}
