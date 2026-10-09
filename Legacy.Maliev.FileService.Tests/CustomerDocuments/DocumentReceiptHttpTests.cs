using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class DocumentReceiptHttpTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task VerificationRoute_RequiresCurrentVerifyAuthority(bool permission, HttpStatusCode expected)
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.AuthenticatedClient(permission, "legacy-file.documents.verify");
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/verification",
            new { ExpectedVerificationRevision = 1L, Status = "Verified", Reason = "Reviewed exact synthetic evidence" });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task VerificationRoute_AnonymousRequiresAuthentication()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/verification",
            new { ExpectedVerificationRevision = 1L, Status = "Verified", Reason = "Synthetic review" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task RegisteredListRoute_UnavailableAuthority_Returns503()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync("/customers/23/documents");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredHistoryRoute_UnavailableAuthority_Returns503()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync($"/customers/23/documents/{Guid.NewGuid()}/versions");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task OrdinaryHostWithoutOwnerRegistration_Returns503()
    {
        await using var factory = new ReceiptFactory(false);
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(Route(23));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
    [Fact]
    public async Task RegisteredReceiptRoute_UnavailableCurrentAuthority_Returns503()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(Route(23));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredReceiptRoute_Anonymous_Returns401()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Route(23));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredReceiptRoute_MissingReadPermission_Returns403()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.AuthenticatedClient(false);
        using var response = await client.GetAsync(Route(23));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredReceiptRoute_NonpositiveCustomer_Returns400()
    {
        await using var factory = new ReceiptFactory();
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(Route(0));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Route(int customerId) => $"/customers/{customerId}/documents/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/receipt";

    private sealed class ReceiptFactory(bool registerModule = true) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);

        public HttpClient AuthenticatedClient(bool permission = true, string permissionName = "legacy-file.documents.read")
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var claims = new List<Claim> { new("sub", "synthetic-employee") };
            if (permission) claims.Add(new("permissions", permissionName));
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid", claims,
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            if (registerModule)
                builder.ConfigureServices(services => services.AddCustomerDocuments("Host=127.0.0.1;Database=synthetic;Username=synthetic;Password=synthetic", enabled: true));
            builder.UseSetting("ConnectionStrings:FileDbContext", "Host=127.0.0.1;Database=synthetic;Username=synthetic;Password=synthetic");
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
        }
    }
}
