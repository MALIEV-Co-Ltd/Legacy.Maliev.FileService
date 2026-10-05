using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Actual Program/auth/controller/application/PostgreSQL/GCS adapter and SDK signing template.
// Only GCS SDK/blob-signing effects are controlled; never live cloud or IAM evidence.
[Collection(LegacySignedReadPostgreSqlCollection.Name)]
public sealed class LegacySignedReadHttpBoundaryTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData("/uploads/SignedUrl")]
    [InlineData("/uploads/signedurl/")]
    public async Task ConfirmedRecord_UsesActualReadAdmissionAndJsonUriContract(string route)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var response = await client.GetAsync(Query(route, name));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uri = await response.Content.ReadFromJsonAsync<Uri>();
        Assert.NotNull(uri);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("storage.googleapis.com", uri.Host);
        Assert.Contains("X-Goog-Expires=604800", uri.Query, StringComparison.Ordinal);
        Assert.Contains("response-content-disposition=", uri.Query, StringComparison.Ordinal);
        Assert.Equal(1, factory.SignCalls);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
    }

    [Fact]
    public async Task MissingMetadata_Returns404WithoutSigningOrCloudLookup()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        Assert.Equal(0, factory.SignCalls);
        Assert.Equal(0, factory.GetCalls);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("wrong-key", 401)]
    [InlineData("no-permission", 403)]
    [InlineData("wrong-permission", 403)]
    public async Task RealAdmissionFailure_HasNoStorageOrSigningEffect(string identity, int status)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client(identity);
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
    }

    [Theory]
    [InlineData("name-only")]
    [InlineData("missing-live")]
    [InlineData("generation-replaced")]
    [InlineData("not-clean")]
    [InlineData("quarantine")]
    [InlineData("revoked-journal")]
    public async Task Characterization_NameMetadataStillSignsWithoutGenerationObservation(string state)
    {
        await using var context = await ContextAsync();
        var name = (state == "quarantine" ? "_quarantine/" : "") + Name();
        await SeedAsync(context, name, state);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name)
        { LiveMissing = state == "missing-live", LiveGeneration = state == "generation-replaced" ? 47 : 31 };
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        // Observed existing/source compatibility, not certification of scan or generation safety.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uri = await response.Content.ReadFromJsonAsync<Uri>();
        Assert.NotNull(uri);
        Assert.DoesNotContain("generation=", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.SignCalls);
        Assert.Equal(0, factory.GetCalls);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
    }

    [Fact]
    public async Task MetadataDeleted_AfterPriorSignedRead_NextRequestIs404WithoutCachedUrl()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var first = await client.GetAsync(Query("/uploads/SignedUrl", name));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await context.Uploads.Where(row => row.Bucket == "private" && row.Name == name).ExecuteDeleteAsync();
        using var second = await client.GetAsync(Query("/uploads/SignedUrl", name));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(1, factory.SignCalls);
        Assert.Equal(0, factory.GetCalls);
        // Removing metadata prevents a new URL; previously issued cloud URLs are not thereby revoked.
    }

    [Fact]
    public async Task SigningDependencyFailure_RemainsOpaqueServerFailureNot404OrNullUri()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name) { FailSigning = true };
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-signing-fixture", body, StringComparison.Ordinal);
        Assert.DoesNotContain(name, body, StringComparison.Ordinal);
        Assert.Equal(1, factory.SignCalls);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
    }

    private async Task<FileDbContext> ContextAsync()
    {
        var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static string Name() => "orders/" + Guid.NewGuid().ToString("N") + "/ชิ้นงาน.step";
    private static string Query(string route, string name) => route + "?bucket=private&objectName=" + Uri.EscapeDataString(name);

    private static async Task SeedAsync(FileDbContext context, string name, string state = "clean")
    {
        context.Uploads.Add(new Upload { Bucket = "private", Name = name, ContentType = "application/octet-stream", Size = 7 });
        if (state != "name-only") context.StorageMoveJournals.Add(new StorageMoveJournal
        {
            OperationId = Guid.NewGuid(), ScanClean = state != "not-clean", SourceBucket = "private", SourceObjectName = "_quarantine/" + name,
            SourceGeneration = 17, DestinationBucket = "private", DestinationObjectName = name, DestinationGeneration = 31,
            State = state == "revoked-journal" ? "CompensatedRemoved" : "MetadataCommitted", CreatedAt = DateTimeOffset.UtcNow, ModifiedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private sealed class SignedReadFactory(string connection, string name) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        private readonly string principal = "signed-read-" + Guid.NewGuid().ToString("N");
        public bool LiveMissing { get; init; }
        public long LiveGeneration { get; init; } = 31;
        public bool FailSigning { get; init; }
        public int GetCalls { get; private set; }
        public int SignCalls { get; private set; }

        public HttpClient Client(string identity = "read")
        {
            var client = CreateClient();
            if (identity == "anonymous") return client;
            using var wrongKey = identity == "wrong-key" ? RSA.Create(2048) : null;
            var now = DateTime.UtcNow;
            // File's unchanged registration has no IAM client. Exercise its real signed-claim admission.
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, principal) };
            if (identity is "read" or "wrong-key") claims.Add(new("permissions", FilePermissions.Read));
            if (identity == "wrong-permission") claims.Add(new("permissions", FilePermissions.Create));
            var token = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                claims, now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(wrongKey ?? key), SecurityAlgorithms.RsaSha256));
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
            builder.UseSetting("FileStorage:WritesEnabled", "false");
            builder.UseSetting("FileStorage:AllowedBuckets:0", "private");
            builder.UseSetting("FileStorage:SignedUrlHours", "168");
            builder.ConfigureServices(services =>
            {
                var sdk = new Mock<StorageClient>(MockBehavior.Strict);
                sdk.Setup(value => value.GetObjectAsync("private", name, It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        GetCalls++;
                        return LiveMissing ? Task.FromException<StorageObject>(new GoogleApiException("storage", "controlled absent object") { HttpStatusCode = HttpStatusCode.NotFound })
                            : Task.FromResult(new StorageObject { Bucket = "private", Name = name, Generation = LiveGeneration, Size = 7 });
                    });
                var signer = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
                signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
                signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
                signer.Setup(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        SignCalls++;
                        return FailSigning ? Task.FromException<string>(new IOException("private-signing-fixture")) : Task.FromResult("AQ==");
                    });
                services.RemoveAll<StorageClient>();
                services.AddSingleton(sdk.Object);
                services.RemoveAll<UrlSigner>();
                services.AddSingleton(UrlSigner.FromBlobSigner(signer.Object));
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
public sealed class LegacySignedReadPostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "LegacySignedReadPostgreSQL";
}
