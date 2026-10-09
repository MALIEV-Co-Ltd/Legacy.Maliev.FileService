using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class ProtectedDocumentDownloadHttpTests
{
    [Fact]
    public async Task AnonymousCannotReachProtectedAttachment()
    {
        await using var host = new ProtectedFactory();
        using var client = host.CreateClient();
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task OrdinaryFilePermissionCannotReachProtectedAttachment()
    {
        await using var host = new ProtectedFactory();
        using var client = host.Client("legacy-file.uploads.read");
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task DisabledProtectedRuntimeReturnsUnavailableWithoutBytesOrUrl()
    {
        await using var host = new ProtectedFactory();
        using var client = host.Client("legacy-file.documents.read");
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }
    private static string Route => $"/customers/23/documents/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/download";
    private sealed class ProtectedFactory : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public HttpClient Client(string permission)
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var claims = new[] { new Claim("sub", "synthetic-employee"), new Claim("permissions", permission) };
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid", claims, now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.UseSetting("ConnectionStrings:FileDbContext", "Host=127.0.0.1;Database=synthetic;Username=synthetic");
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
        }
    }
}
