using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class ProtectedDocumentBypassHttpTests
{
    [Theory]
    [InlineData("GET", "/uploads/SignedUrl?bucket=synthetic&objectName=customer-documents/23/original", "legacy-file.uploads.read")]
    [InlineData("GET", "/uploads/SignedUrl?bucket=synthetic&objectName=%2Fcustomer-documents%2F23%2Foriginal", "legacy-file.uploads.read")]
    [InlineData("DELETE", "/Uploads?bucket=synthetic&objectName=customer-documents/23/original", "legacy-file.uploads.delete")]
    [InlineData("PUT", "/Uploads?sourceBucket=synthetic&sourceObjectName=customer-documents/23/original&destinationBucket=synthetic&destinationObjectName=other", "legacy-file.uploads.update")]
    [InlineData("PUT", "/Uploads?sourceBucket=synthetic&sourceObjectName=other&destinationBucket=synthetic&destinationObjectName=customer-documents/23/original", "legacy-file.uploads.update")]
    public async Task LegacyGenericOperationsRefuseReservedOriginals(string method, string route, string permission)
    {
        await using var host = new Factory();
        using var client = host.Client(permission);
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("signed.example.invalid", await response.Content.ReadAsStringAsync());
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public HttpClient Client(string permission)
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var claims = new[] { new Claim("sub", "synthetic-staff"), new Claim("permissions", permission) };
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
            builder.ConfigureServices(services =>
            {
                services.Configure<FileStorageOptions>(value => { value.Enabled = true; value.WritesEnabled = true; value.AllowedBuckets = ["synthetic"]; });
                var storage = new Mock<IObjectStorage>();
                storage.Setup(x => x.GetEvidenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StorageObjectEvidence(71, 3));
                storage.Setup(x => x.CreateSignedGenerationReadUriAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Uri("https://signed.example.invalid/private"));
                storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
                var uploads = new Mock<IUploadRepository>();
                uploads.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
                var read = new Mock<IStorageReadJournal>();
                read.Setup(x => x.FindReadEvidenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StorageReadEvidence(StorageReadState.Absent));
                services.RemoveAll<IObjectStorage>(); services.AddSingleton(storage.Object);
                services.RemoveAll<IUploadRepository>(); services.AddSingleton(uploads.Object);
                services.RemoveAll<IStorageReadJournal>(); services.AddSingleton(read.Object);
            });
        }
    }
}
