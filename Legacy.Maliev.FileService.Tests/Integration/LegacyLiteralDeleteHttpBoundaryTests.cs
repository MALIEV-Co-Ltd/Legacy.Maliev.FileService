using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Real host/auth/controller/application/PostgreSQL/GCS adapter; controlled SDK deletion only.
[Collection(LegacyConsumerWritePostgreSqlCollection.Name)]
public sealed class LegacyLiteralDeleteHttpBoundaryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public Task LiteralOnly_DeletesExactObjectAndMetadata() => AssertDeleteAsync(true, false);

    [Fact]
    public Task LiteralAndDecoy_DeletesLiteralAndRetainsDecoy() => AssertDeleteAsync(true, true);

    [Fact]
    public Task MissingLiteralWithDecoy_LeavesProviderAndMetadataUntouched() => AssertDeleteAsync(false, true);

    private async Task AssertDeleteAsync(bool literalExists, bool decoyExists)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync(deadline.Token);
        var decoy = "orders/" + Guid.NewGuid().ToString("N") + "/ชิ้นงาน-cafe\u0301.step";
        var literal = "  " + decoy + "  ";
        if (literalExists) context.Uploads.Add(new Upload { Bucket = "maliev.com", Name = literal, Size = 7 });
        if (decoyExists) context.Uploads.Add(new Upload { Bucket = "maliev.com", Name = decoy, Size = 11 });
        await context.SaveChangesAsync(deadline.Token);
        await using var factory = new DeleteFactory(context.Database.GetConnectionString()!, literal, decoy,
            literalExists, decoyExists);
        using var client = factory.Client();
        using var response = await client.DeleteAsync("/Uploads?bucket=maliev.com&objectName=" + Uri.EscapeDataString(literal), deadline.Token);

        Assert.Equal(literalExists ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(literalExists ? new[] { literal } : Array.Empty<string>(), factory.Deletes);
        Assert.DoesNotContain(literal, factory.Objects);
        Assert.Equal(decoyExists, factory.Objects.Contains(decoy));
        Assert.False(await context.Uploads.AsNoTracking().AnyAsync(row => row.Bucket == "maliev.com" && row.Name == literal, deadline.Token));
        Assert.Equal(decoyExists, await context.Uploads.AsNoTracking().AnyAsync(row => row.Bucket == "maliev.com" && row.Name == decoy, deadline.Token));
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
    }

    private sealed class DeleteFactory(string connection, string literal, string decoy,
        bool literalExists, bool decoyExists) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Deletes { get; } = [];

        public HttpClient Client()
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                [new Claim("sub", "literal-delete-fixture"), new Claim("permissions", FilePermissions.Delete)],
                now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
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
            builder.UseSetting("FileStorage:AllowedBuckets:0", "maliev.com");
            builder.ConfigureServices(services =>
            {
                if (literalExists) Objects.Add(literal);
                if (decoyExists) Objects.Add(decoy);
                var sdk = new Mock<StorageClient>(MockBehavior.Strict);
                sdk.Setup(value => value.DeleteObjectAsync("maliev.com", It.IsAny<string>(),
                        It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
                    .Returns<string, string, DeleteObjectOptions, CancellationToken>((_, name, options, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        Assert.Null(options);
                        Deletes.Add(name);
                        Assert.True(Objects.Remove(name), "Delete selected an absent object.");
                        return Task.CompletedTask;
                    });
                var signer = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
                signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
                signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
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
