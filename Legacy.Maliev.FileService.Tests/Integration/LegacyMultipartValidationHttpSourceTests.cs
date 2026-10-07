using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Source oracle: original UploadsController.ValidFiles at checkpoint135e526.
// Real multipart binding is distinguished from direct constructed-IFormFile tests.
[Collection(LegacyMultipartPostgreSqlCollection.Name)]
public sealed class LegacyMultipartValidationHttpSourceTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData("no-files", "Files must not be empty")]
    [InlineData("wrong-field", "Files must not be empty")]
    [InlineData("nameless-form-field", "Files must not be empty")]
    [InlineData("empty-file", "File have length")]
    [InlineData("uppercase-empty-file", "File have length")]
    [InlineData("valid-plus-empty-file", "File have length")]
    public async Task EnabledRuntime_ActualMultipartBindingPreservesOriginalValidationMessageBeforeEffects(string shape, string message)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        await using var factory = new MultipartFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        using var body = Multipart(shape);
        using var response = await client.PostAsync("/Uploads?bucket=private&path=orders", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        var errors = json.RootElement.EnumerateArray().SelectMany(group => group.EnumerateArray()).ToArray();
        var error = Assert.Single(errors);
        Assert.Equal(message, error.GetProperty("ErrorMessage").GetString());
        Assert.False(error.TryGetProperty("errorMessage", out _));
        Assert.False(error.TryGetProperty("Exception", out _));
        await AssertRealBoundaryWithoutEffectsAsync(factory, context);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("wrong-key", 401)]
    [InlineData("no-grant", 403)]
    public async Task EnabledRuntime_ActualAdmissionPrecedesMultipartValidationWithoutLeakingItsMessage(string identity, int expected)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        await using var factory = new MultipartFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client(identity);
        using var body = Multipart("empty-file");
        using var response = await client.PostAsync("/Uploads?bucket=private", body);

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.DoesNotContain("File have length", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertRealBoundaryWithoutEffectsAsync(factory, context);
    }

    [Theory]
    [InlineData(1024, false, false)]
    [InlineData(1025, true, false)]
    [InlineData(1024, false, true)]
    [InlineData(1025, true, true)]
    public async Task MultipartSectionCount_HasExplicitBoundBeforeStorageEffects(int count, bool parserRejects, bool mixed)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        await using var factory = new MultipartFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        client.Timeout = TimeSpan.FromSeconds(30);
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Features.FormOptions>>().Value;
        Assert.Equal(1024, options.ValueCountLimit);
        Assert.Equal(4 * 1024 * 1024, options.ValueLengthLimit);
        Assert.Equal(FileApplicationService.MaximumRequestBytes, options.MultipartBodyLengthLimit);
        using var body = new MultipartFormDataContent();
        for (var index = 0; index < count; index++)
        {
            if (mixed && index % 2 == 1)
            {
                body.Add(new StringContent("ignored"), "description");
            }
            else
            {
                body.Add(new ByteArrayContent([]), "files", "empty.step");
            }
        }

        using var response = await client.PostAsync("/Uploads?bucket=private", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(parserRejects ? JsonValueKind.Object : JsonValueKind.Array, json.RootElement.ValueKind);
        if (!parserRejects)
        {
            Assert.Contains("File have length", json.RootElement.ToString(), StringComparison.Ordinal);
        }

        await AssertRealBoundaryWithoutEffectsAsync(factory, context);
    }

    [Theory]
    [InlineData(4194304, false)]
    [InlineData(4194305, true)]
    public async Task UrlEncodedValueLength_HasExplicitBoundBeforeStorageEffects(int length, bool parserRejects)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        await using var factory = new MultipartFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var body = new FormUrlEncodedContent([new KeyValuePair<string, string>("description", new string('a', length))]);
        using var response = await client.PostAsync("/Uploads?bucket=private", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(parserRejects ? JsonValueKind.Object : JsonValueKind.Array, json.RootElement.ValueKind);
        if (!parserRejects)
        {
            Assert.Contains("Files must not be empty", json.RootElement.ToString(), StringComparison.Ordinal);
        }

        await AssertRealBoundaryWithoutEffectsAsync(factory, context);
    }

    private static MultipartFormDataContent Multipart(string shape)
    {
        var body = new MultipartFormDataContent();
        if (shape == "no-files") body.Add(new StringContent("orders"), "description");
        else if (shape == "nameless-form-field") body.Add(new ByteArrayContent([1]), "files");
        else
        {
            if (shape == "valid-plus-empty-file") body.Add(new ByteArrayContent([1]), "files", "first.step");
            body.Add(new ByteArrayContent([]), shape switch
            {
                "wrong-field" => "upload",
                "uppercase-empty-file" => "FILES",
                _ => "files",
            }, "empty.step");
        }
        return body;
    }

    private static async Task AssertRealBoundaryWithoutEffectsAsync(MultipartFactory factory, FileDbContext context)
    {
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
        Assert.IsType<StorageMoveJournalRepository>(scope.ServiceProvider.GetRequiredService<IStorageMoveJournal>());
        factory.Sdk.VerifyNoOtherCalls();
        factory.Scanner.VerifyNoOtherCalls();
        factory.Checkpoints.VerifyNoOtherCalls();
        factory.Signer.Verify(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await context.Uploads.AnyAsync());
        Assert.False(await context.StorageMoveJournals.AnyAsync());
        Assert.False(await context.QuarantineUploadIntents.AnyAsync());
    }

    private sealed class MultipartFactory(string connection) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public Mock<StorageClient> Sdk { get; } = new(MockBehavior.Strict);
        public Mock<IFileSafetyScanner> Scanner { get; } = new(MockBehavior.Strict);
        public Mock<IUploadIdempotencyStore> Checkpoints { get; } = new(MockBehavior.Strict);
        public Mock<UrlSigner.IBlobSigner> Signer { get; } = new(MockBehavior.Strict);

        public HttpClient Client(string identity = "create")
        {
            var client = CreateClient();
            if (identity == "anonymous") return client;
            using var wrongKey = identity == "wrong-key" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "multipart-source-fixture") };
            if (identity is "create" or "wrong-key") claims.Add(new("permissions", FilePermissions.Create));
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid", claims,
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(wrongKey ?? key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:FileDbContext", connection);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.UseSetting("FileStorage:Enabled", "true");
            builder.UseSetting("FileStorage:WritesEnabled", "true");
            builder.UseSetting("FileStorage:AllowedBuckets:0", "private");
            builder.ConfigureServices(services =>
            {
                Signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
                Signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
                services.RemoveAll<StorageClient>();
                services.AddSingleton(Sdk.Object);
                services.RemoveAll<UrlSigner>();
                services.AddSingleton(UrlSigner.FromBlobSigner(Signer.Object));
                services.RemoveAll<IFileSafetyScanner>();
                services.AddSingleton(Scanner.Object);
                services.RemoveAll<IUploadIdempotencyStore>();
                services.AddSingleton(Checkpoints.Object);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) key.Dispose();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LegacyMultipartPostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "LegacyMultipartPostgreSQL";
}
