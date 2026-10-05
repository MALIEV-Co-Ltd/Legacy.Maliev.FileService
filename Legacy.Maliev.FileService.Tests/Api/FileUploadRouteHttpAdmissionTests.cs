using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Tests.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Api;

// Actual Program/JWT/controller/resource-filter admission with storage and writes
// disabled. The strict application boundary must never be called in these cases.
[Collection(FileIncidentHttpCollection.Name)]
public sealed class FileUploadRouteHttpAdmissionTests(FileOpenApiPostgresFixture database)
    : IClassFixture<FileOpenApiPostgresFixture>
{
    [Theory]
    [InlineData("POST", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "without-required-permission", HttpStatusCode.Forbidden)]
    [InlineData("POST", "required-permission", HttpStatusCode.ServiceUnavailable)]
    [InlineData("PUT", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("PUT", "wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("PUT", "without-required-permission", HttpStatusCode.Forbidden)]
    [InlineData("PUT", "required-permission", HttpStatusCode.BadRequest)]
    [InlineData("DELETE", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("DELETE", "wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("DELETE", "without-required-permission", HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "required-permission", HttpStatusCode.BadRequest)]
    public async Task ActualUploadRoutes_EnforceSignedPermissionBeforeDisabledWritesOrMissingQuery(
        string method, string identity, HttpStatusCode expectedStatus)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (identity != "anonymous") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(identity, method));
        using var request = new HttpRequestMessage(new HttpMethod(method), "/Uploads");
        if (method == "POST")
        {
            // Intentionally malformed multipart: an admitted request must receive
            // the disabled-write response before a model-binding error response.
            // This asserts response precedence, not transport-level byte reads.
            request.Content = new ByteArrayContent([1]);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        }
        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.Unauthorized)
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
        if (expectedStatus == HttpStatusCode.ServiceUnavailable)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(503, problem.GetProperty("status").GetInt32());
            Assert.Equal("Legacy file service unavailable", problem.GetProperty("title").GetString());
            Assert.Equal("File storage is temporarily unavailable.", problem.GetProperty("detail").GetString());
        }
        if (expectedStatus == HttpStatusCode.BadRequest)
        {
            var expectedMessage = method == "DELETE"
                ? "Bucket and object name is required"
                : "Bucket and object names are required";
            Assert.Equal(expectedMessage, await response.Content.ReadFromJsonAsync<string>());
        }
        Assert.Null(factory.Services.GetService<StorageClient>());
        factory.Service.VerifyNoOtherCalls();
    }

    private sealed class AdmissionFactory(string connectionString) : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://file-route-fixture.invalid";
        private const string Audience = "file-route-fixture";
        private readonly RSA signingKey = RSA.Create(2048);
        public Mock<IFileService> Service { get; } = new(MockBehavior.Strict);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:FileDbContext"] = connectionString,
                ["Cache:RedisEnabled"] = "false",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["FileStorage:Enabled"] = "false",
                ["FileStorage:WritesEnabled"] = "false",
                ["InstantQuoteFiles:Enabled"] = "false",
                ["InstantQuoteFiles:WritesEnabled"] = "false",
                ["InstantQuoteFiles:CleanupEnabled"] = "false",
            };
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFileService>();
                services.AddSingleton(Service.Object);
            });
        }

        public string Token(string identity, string method)
        {
            using var wrongKey = identity == "wrong-signature" ? RSA.Create(2048) : null;
            var permission = method switch
            {
                "POST" => FilePermissions.Create,
                "PUT" => FilePermissions.Update,
                "DELETE" => FilePermissions.Delete,
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            };
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, "file-route-fixture"),
                // Read permission alone must not admit create/update/delete.
                new("permissions", identity == "without-required-permission" ? FilePermissions.Read : permission),
            };
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(wrongKey ?? signingKey), SecurityAlgorithms.RsaSha256)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) signingKey.Dispose();
        }
    }
}
