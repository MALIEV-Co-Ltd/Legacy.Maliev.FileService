using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.FileService.Tests.OpenApi;

// Uses the actual Program, controllers, documentation transformers and JWT middleware.
// No authentication/controller/storage service replacement or cloud credential is registered.
public sealed class LegacyFileStartupHttpAcceptanceTests(FileOpenApiPostgresFixture database)
    : IClassFixture<FileOpenApiPostgresFixture>
{
    [Fact]
    public async Task DevelopmentOpenApi_RetainsOriginalLegacyQueryDescriptions()
    {
        await using var factory = new FileStartupFactory(database.ConnectionString, "Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/file/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        (string Path, string Method, string Parameter, string Description)[] expected =
        [
            ("/Uploads", "post", "bucket", "Name of the bucket."),
            ("/Uploads", "post", "path", "The path."),
            ("/Uploads", "delete", "bucket", "The bucket."),
            ("/Uploads", "delete", "objectName", "Name of the object."),
            ("/Uploads", "put", "sourceBucket", "The bucket."),
            ("/Uploads", "put", "sourceObjectName", "Name of the source object."),
            ("/Uploads", "put", "destinationBucket", "The destination bucket."),
            ("/Uploads", "put", "destinationObjectName", "Name of the destination object."),
            ("/uploads/SignedUrl", "get", "bucket", "The bucket."),
            ("/uploads/SignedUrl", "get", "objectName", "Name of the object."),
        ];
        foreach (var contract in expected)
        {
            var operation = paths.GetProperty(contract.Path).GetProperty(contract.Method);
            Assert.True(operation.TryGetProperty("summary", out var summary), contract.Path);
            Assert.False(string.IsNullOrWhiteSpace(summary.GetString()));
            var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
                item => item.GetProperty("name").GetString() == contract.Parameter);
            Assert.True(parameter.TryGetProperty("description", out var description),
                $"Original XML query description is missing: {contract.Method} {contract.Path} {contract.Parameter}");
            Assert.Equal(contract.Description, description.GetString());
        }
    }

    [Fact]
    public async Task ProductionOpenApi_IsNotExposed()
    {
        await using var factory = new FileStartupFactory(database.ConnectionString, "Production");
        using var response = await factory.CreateClient().GetAsync("/file/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CurrentIntranetSignedUrlQuery_ReportsDisabledStorageAfterRealReadAdmission()
    {
        await using var factory = new FileStartupFactory(database.ConnectionString, "Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("read"));
        using var response = await client.GetAsync(
            "/uploads/SignedUrl?bucket=maliev.com&objectName=orders%2F37%2Fdrawing.stl");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("Legacy file service unavailable", problem.GetProperty("title").GetString());
        // This proves the disabled runtime response, not active storage, IAM, or metadata parity.
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("without-permission", HttpStatusCode.Forbidden)]
    [InlineData("read", HttpStatusCode.BadRequest)]
    public async Task SignedUrl_UsesRealSignatureAndPermissionAdmissionBeforeMissingQueryBinding(
        string identity, HttpStatusCode expectedStatus)
    {
        await using var factory = new FileStartupFactory(database.ConnectionString, "Production");
        using var client = factory.CreateClient();
        if (identity != "anonymous") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(identity));
        using var response = await client.GetAsync("/uploads/SignedUrl");

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.Unauthorized)
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
    }

    private sealed class FileStartupFactory(string connectionString, string environment) : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://file-startup-fixture.invalid";
        private const string Audience = "file-startup-fixture";
        private readonly RSA signingKey = RSA.Create(2048);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
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
        }

        public string Token(string identity)
        {
            using var wrongKey = identity == "wrong-signature" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "file-startup-fixture") };
            if (identity is "read" or "wrong-signature") claims.Add(new("permissions", FilePermissions.Read));
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

public sealed class FileOpenApiPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public string ConnectionString => postgres.GetConnectionString();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
}
