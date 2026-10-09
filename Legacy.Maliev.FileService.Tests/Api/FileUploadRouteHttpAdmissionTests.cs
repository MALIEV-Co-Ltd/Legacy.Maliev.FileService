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
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Tests.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Api;

// Actual Program/JWT/controller/resource-filter admission with storage and writes
// disabled. Admission cases never call the strict application boundary; query
// handoff cases control that boundary, and gate cases retain the actual service.
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

    [Theory]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    public async Task ValidLegacyQuery_ActualApplicationRejectsDisabledWrites(string method)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString, useActualApplication: true);
        using var client = AuthorizedClient(factory, method);
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.Null(factory.Services.GetService<StorageClient>());

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), LegacyQuery(method)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("Legacy file service unavailable", problem.GetProperty("title").GetString());
        Assert.Equal("File storage is temporarily unavailable.", problem.GetProperty("detail").GetString());
        factory.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DELETE", "bucket")]
    [InlineData("DELETE", "objectName")]
    [InlineData("PUT", "sourceBucket")]
    [InlineData("PUT", "sourceObjectName")]
    [InlineData("PUT", "destinationBucket")]
    [InlineData("PUT", "destinationObjectName")]
    public async Task EachMissingLegacyQueryField_RejectsBeforeApplication(string method, string missingField)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString);
        using var client = AuthorizedClient(factory, method);
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), LegacyQuery(method, missingField)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(method == "DELETE" ? "Bucket and object name is required" : "Bucket and object names are required",
            await response.Content.ReadFromJsonAsync<string>());
        factory.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DELETE", true)]
    [InlineData("DELETE", false)]
    [InlineData("PUT", true)]
    [InlineData("PUT", false)]
    public async Task ValidLegacyQuery_HandsDecodedFieldsToControlledApplication(string method, bool succeeds)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString);
        if (method == "DELETE")
            factory.Service.Setup(service => service.DeleteAsync("source-bucket", "folder/source +ไทย.txt", It.IsAny<CancellationToken>()))
                .ReturnsAsync(succeeds);
        else
            factory.Service.Setup(service => service.MoveAsync("source-bucket", "folder/source +ไทย.txt",
                "destination-bucket", "folder/destination +ไทย.txt", It.IsAny<CancellationToken>()))
                .ReturnsAsync(succeeds);
        using var client = AuthorizedClient(factory, method);
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), LegacyQuery(method)));

        Assert.Equal(succeeds ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest, response.StatusCode);
        if (succeeds) Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        else Assert.Equal(method == "DELETE" ? "Could not delete uploaded file" : "Could not move the uploaded file",
            await response.Content.ReadFromJsonAsync<string>());
        factory.Service.VerifyAll();
        if (method == "DELETE")
            factory.Service.Verify(service => service.DeleteAsync("source-bucket", "folder/source +ไทย.txt", It.IsAny<CancellationToken>()), Times.Once);
        else
            factory.Service.Verify(service => service.MoveAsync("source-bucket", "folder/source +ไทย.txt",
                "destination-bucket", "folder/destination +ไทย.txt", It.IsAny<CancellationToken>()), Times.Once);
        factory.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DELETE", "bucket", "same")]
    [InlineData("DELETE", "Bucket", "different")]
    [InlineData("DELETE", "BUCKET", "empty")]
    [InlineData("DELETE", "objectName", "same")]
    [InlineData("DELETE", "ObjectName", "different")]
    [InlineData("DELETE", "OBJECTNAME", "empty")]
    [InlineData("PUT", "sourceBucket", "same")]
    [InlineData("PUT", "SourceBucket", "different")]
    [InlineData("PUT", "SOURCEBUCKET", "empty")]
    [InlineData("PUT", "sourceObjectName", "same")]
    [InlineData("PUT", "SourceObjectName", "different")]
    [InlineData("PUT", "SOURCEOBJECTNAME", "empty")]
    [InlineData("PUT", "destinationBucket", "same")]
    [InlineData("PUT", "DestinationBucket", "different")]
    [InlineData("PUT", "DESTINATIONBUCKET", "empty")]
    [InlineData("PUT", "destinationObjectName", "same")]
    [InlineData("PUT", "DestinationObjectName", "different")]
    [InlineData("PUT", "DESTINATIONOBJECTNAME", "empty")]
    public async Task RepeatedMutationCoordinate_RejectsBeforeApplication(string method, string field, string variant)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString);
        using var client = AuthorizedClient(factory, method);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), RepeatedQuery(method, field, variant)), deadline.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(method == "DELETE" ? "Bucket and object name is required" : "Bucket and object names are required",
            await response.Content.ReadFromJsonAsync<string>());
        factory.Service.VerifyNoOtherCalls();
        Assert.Null(factory.Services.GetService<StorageClient>());
    }

    [Theory]
    [InlineData("DELETE", "bucket")]
    [InlineData("DELETE", "objectName")]
    [InlineData("PUT", "sourceBucket")]
    [InlineData("PUT", "sourceObjectName")]
    [InlineData("PUT", "destinationBucket")]
    [InlineData("PUT", "destinationObjectName")]
    public async Task RepeatedMutationCoordinate_RejectsBeforeActualDisabledApplication(string method, string field)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString, useActualApplication: true);
        using var client = AuthorizedClient(factory, method);
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        var context = scope.ServiceProvider.GetRequiredService<FileDbContext>();
        await context.Database.MigrateAsync();
        context.Uploads.Add(new()
        {
            Bucket = "source-bucket", Name = "folder/source +ไทย.txt", Size = 7, ContentType = "text/plain",
        });
        context.StorageMoveJournals.Add(new()
        {
            OperationId = Guid.NewGuid(), ScanClean = true,
            SourceBucket = "source-bucket", SourceObjectName = "_quarantine/folder/source +ไทย.txt", SourceGeneration = 17,
            DestinationBucket = "source-bucket", DestinationObjectName = "folder/source +ไทย.txt", DestinationGeneration = 31,
            State = "MetadataCommitted", CreatedAt = DateTimeOffset.UtcNow, ModifiedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        var uploadsBefore = await context.Uploads.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        var journalsBefore = await context.StorageMoveJournals.AsNoTracking().OrderBy(row => row.OperationId).ToArrayAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), RepeatedQuery(method, field, "different")), deadline.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        factory.Service.VerifyNoOtherCalls();
        Assert.Null(factory.Services.GetService<StorageClient>());
        var uploadsAfter = await context.Uploads.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        var journalsAfter = await context.StorageMoveJournals.AsNoTracking().OrderBy(row => row.OperationId).ToArrayAsync();
        Assert.Equal(JsonSerializer.Serialize(uploadsBefore), JsonSerializer.Serialize(uploadsAfter));
        Assert.Equal(JsonSerializer.Serialize(journalsBefore), JsonSerializer.Serialize(journalsAfter));
    }

    [Theory]
    [InlineData("DELETE", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("DELETE", "wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("DELETE", "without-required-permission", HttpStatusCode.Forbidden)]
    [InlineData("PUT", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("PUT", "wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("PUT", "without-required-permission", HttpStatusCode.Forbidden)]
    public async Task RepeatedMutationCoordinate_RetainsPermissionPrecedence(string method, string identity, HttpStatusCode expected)
    {
        await using var factory = new AdmissionFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        if (identity != "anonymous") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(identity, method));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            RepeatedQuery(method, method == "DELETE" ? "bucket" : "sourceBucket", "different")), deadline.Token);

        Assert.Equal(expected, response.StatusCode);
        factory.Service.VerifyNoOtherCalls();
    }

    private static string RepeatedQuery(string method, string field, string variant)
    {
        var original = field.EndsWith("Bucket", StringComparison.OrdinalIgnoreCase)
            ? field.StartsWith("destination", StringComparison.OrdinalIgnoreCase) ? "destination-bucket" : "source-bucket"
            : field.StartsWith("destination", StringComparison.OrdinalIgnoreCase) ? "folder/destination +ไทย.txt" : "folder/source +ไทย.txt";
        var value = variant == "same" ? original : variant == "empty" ? "" : "different-coordinate";
        return LegacyQuery(method) + "&" + field + "=" + Uri.EscapeDataString(value);
    }

    private static HttpClient AuthorizedClient(AdmissionFactory factory, string method)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("required-permission", method));
        return client;
    }

    private static string LegacyQuery(string method, string? missingField = null)
    {
        var fields = method == "DELETE"
            ? new Dictionary<string, string> { ["bucket"] = "source-bucket", ["objectName"] = "folder/source +ไทย.txt" }
            : new Dictionary<string, string>
            {
                ["sourceBucket"] = "source-bucket",
                ["sourceObjectName"] = "folder/source +ไทย.txt",
                ["destinationBucket"] = "destination-bucket",
                ["destinationObjectName"] = "folder/destination +ไทย.txt",
            };
        return "/Uploads?" + string.Join("&", fields.Where(field => field.Key != missingField)
            .Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value)));
    }

    private sealed class AdmissionFactory(string connectionString, bool useActualApplication = false) : WebApplicationFactory<Program>
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
            if (!useActualApplication)
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
