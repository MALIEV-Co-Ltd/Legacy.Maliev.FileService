using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class DocumentProtectionHttpTests
{
    [Fact]
    public async Task NdaLifecycleReadUnavailableWithoutRegistration()
    {
        await using var factory = new NdaHttpFactory();
        using var client = factory.AuthenticatedClient("legacy-file.documents.read");
        using var response = await client.GetAsync("customers/23/documents/11111111-1111-1111-1111-111111111111/nda");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousNdaLifecycleReadRequiresAuthentication()
    {
        await using var factory = new NdaHttpFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("customers/23/documents/11111111-1111-1111-1111-111111111111/nda");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Theory]
    [InlineData("legacy-file.protection.evaluate", "customers/23/protection/evaluate", "{\"Kind\":\"Order\",\"ResourceId\":12,\"Action\":\"PublishSocial\",\"SocialConsent\":true}")]
    [InlineData("legacy-file.documents.verify", "customers/23/documents/11111111-1111-1111-1111-111111111111/nda/verification", "{\"VersionId\":\"22222222-2222-2222-2222-222222222222\",\"ExpectedRevision\":1,\"PartyOne\":\"Synthetic A\",\"PartyTwo\":\"Synthetic B\",\"EffectiveAtUtc\":\"2026-01-01T00:00:00Z\",\"SurvivalKind\":\"Unknown\",\"ResponsibleEmployeeSubject\":\"synthetic-responsible\",\"Coverage\":[{\"Kind\":\"Order\",\"ResourceId\":12}],\"Reason\":\"Reviewed synthetic agreement\"}")]
    public async Task UnregisteredNdaBoundaryFailsClosed(string permission, string path, string body)
    {
        await using var factory = new NdaHttpFactory();
        using var client = factory.AuthenticatedClient(permission);
        using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReminderWorklistIsUnavailableWithoutCurrentAuthority()
    {
        await using var factory = new NdaHttpFactory();
        using var client = factory.AuthenticatedClient("legacy-file.documents.read");
        using var response = await client.GetAsync("staff/nda-reminders");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("customers/23/protection/evaluate")]
    [InlineData("customers/23/documents/11111111-1111-1111-1111-111111111111/nda/verification")]
    public async Task AnonymousNdaRoutesRequireAuthentication(string path)
    {
        await using var factory = new NdaHttpFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

internal sealed class NdaHttpFactory(Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
{
    private readonly RSA key = RSA.Create(2048);
    public HttpClient AuthenticatedClient(string? permission)
    {
        var client = CreateClient();
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("sub", "synthetic-employee") };
        if (permission is not null) claims.Add(new("permissions", permission));
        var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid", claims,
            now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        if (configure is not null) builder.ConfigureServices(configure);
        builder.UseSetting("ConnectionStrings:FileDbContext", "Host=127.0.0.1;Database=synthetic;Username=synthetic;Password=synthetic");
        builder.UseSetting("Cache:RedisEnabled", "false");
        builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
        builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
        builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
    }
}
