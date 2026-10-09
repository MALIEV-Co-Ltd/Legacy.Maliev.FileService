using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Real PostgreSQL registry and real protected storage adapter; provider and journal boundaries
// are synthetic controls. These tests do not establish cloud/current-session integration.
[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class DocumentContentEvidenceTests(CustomerDocumentPostgreSqlFixture fixture)
{
    private static readonly byte[] Bytes = "%PDF-1.7\nsynthetic signed agreement\n%%EOF"u8.ToArray();
    private static string Digest => Convert.ToHexStringLower(SHA256.HashData(Bytes));

    [Fact]
    public async Task ComposedDisabledRegistry_ResolvesRealClientWithoutStorageOrDatabaseAccess()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCustomerDocuments("Host=127.0.0.1;Port=1;Database=synthetic;Username=synthetic;Password=synthetic;Timeout=1");
        services.AddProtectedCustomerDocuments();
        services.AddProtectedCustomerDocumentContentEvidence();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<ICustomerDocumentContentEvidence>();
        Assert.IsType<CustomerDocumentContentEvidenceClient>(evidence);
        Assert.False(scope.ServiceProvider.GetRequiredService<CustomerDocumentRegistryOptions>().Enabled);
        Assert.False(scope.ServiceProvider.GetRequiredService<IOptions<CustomerDocumentOptions>>().Value.Enabled);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, await evidence.ValidateAsync(Guid.NewGuid(), Digest, default));
    }

    [Fact]
    public async Task ComposedUnavailableStorage_RefusesHttpReceiptThroughRealContentClient()
    {
        var version = await SeedAsync();
        await using var host = new EvidenceHost(fixture.Connection, null);
        using var http = host.Client();
        using var response = await http.GetAsync($"/customers/23/documents/{version.DocumentId}/versions/{version.Id}/receipt");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(Digest, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task RegisteredHttpReceipt_UsesPersistedJoinAndRealStorageAdapter(bool replaced, HttpStatusCode expected)
    {
        var version = await SeedAsync();
        var setup = Storage(version, replaced ? "replacement" : null);
        await using var host = new EvidenceHost(fixture.Connection, setup.Adapter);
        using var http = host.Client();
        using var response = await http.GetAsync($"/customers/23/documents/{version.DocumentId}/versions/{version.Id}/receipt");
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(version.StorageBucket, body);
        Assert.DoesNotContain(version.StorageObjectName, body);
        Assert.Null(response.Headers.Location);
        if (replaced) Assert.DoesNotContain(Digest, body);
    }

    [Fact]
    public async Task ExactPersistedVersion_UsesRealReaderAndCommittedCleanProof()
    {
        var version = await SeedAsync();
        var setup = Storage(version);
        await using var db = fixture.CreateContext();
        var client = new CustomerDocumentContentEvidenceClient(db, setup.Adapter, new() { Enabled = true });
        Assert.Equal(DocumentAuthorityOutcome.Allowed, await client.ValidateAsync(version.Id, Digest, default));
        setup.Reader.Verify(x => x.ReadAsync(version.StorageBucket, version.StorageObjectName, 109,
            CustomerDocumentOptions.MaximumBytes, It.IsAny<CancellationToken>()), Times.Once);
        setup.Objects.Verify(x => x.CreateSignedReadUriAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        setup.Objects.Verify(x => x.CreateSignedGenerationReadUriAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("digest")]
    [InlineData("unsealed")]
    [InlineData("disabled")]
    public async Task InvalidRegistryEvidence_RefusesBeforeStorage(string failure)
    {
        var version = await SeedAsync(failure != "unsealed");
        var storage = new Mock<IProtectedDocumentStorage>(MockBehavior.Strict);
        await using var db = fixture.CreateContext();
        var client = new CustomerDocumentContentEvidenceClient(db, storage.Object, new() { Enabled = failure != "disabled" });
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, await client.ValidateAsync(
            failure == "missing" ? Guid.NewGuid() : version.Id, failure == "digest" ? new string('b', 64) : Digest, default));
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PersistedCoordinates_CannotReferenceAnotherCustomersObject()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => SeedAsync(crossCustomerCoordinates: true));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("unclean")]
    [InlineData("pending")]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("replacement")]
    [InlineData("bytes")]
    [InlineData("provider")]
    [InlineData("timeout")]
    [InlineData("sourceLocation")]
    public async Task UnconfirmedCurrentContent_RefusesWithoutHistoricalFallback(string failure)
    {
        var version = await SeedAsync();
        var setup = Storage(version, failure);
        await using var db = fixture.CreateContext();
        var client = new CustomerDocumentContentEvidenceClient(db, setup.Adapter, new() { Enabled = true });
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, await client.ValidateAsync(version.Id, Digest, default));
    }

    [Fact]
    public async Task Cancellation_PropagatesInsteadOfBecomingUnavailable()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var db = fixture.CreateContext();
        var client = new CustomerDocumentContentEvidenceClient(db, Mock.Of<IProtectedDocumentStorage>(), new() { Enabled = true });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ValidateAsync(Guid.NewGuid(), Digest, cancelled.Token));
    }

    private async Task<CustomerDocumentVersion> SeedAsync(bool sealedLinks = true, bool crossCustomerCoordinates = false)
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        var document = new CustomerDocument { Id = Guid.NewGuid(), CustomerId = 23, Kind = DocumentKind.Nda,
            Title = "Synthetic evidence", Visibility = DocumentVisibility.Customer };
        var version = new CustomerDocumentVersion { Id = Guid.NewGuid(), DocumentId = document.Id,
            CustomerId = 23, Kind = document.Kind, VersionNumber = 1, ContentSha256 = Digest,
            ActorSubject = "synthetic-uploader", CreatedAtUtc = DateTimeOffset.UtcNow,
            StorageBucket = "synthetic-private", StorageGeneration = 109, ScanSourceGeneration = 103,
            ScanOperationId = Guid.NewGuid(), ContentSize = Bytes.Length, ContentType = "application/pdf", OriginalFileName = "agreement.pdf" };
        version.StorageObjectName = $"customer-documents/{(crossCustomerCoordinates ? 24 : 23)}/{document.Id:N}/{version.Id:N}/original";
        db.AddRange(document, version);
        db.Associations.Add(new() { VersionId = version.Id, CustomerId = 23, Kind = DocumentResourceKind.Customer, ResourceId = 23 });
        await db.SaveChangesAsync();
        if (sealedLinks)
        {
            version.AssociationsSealed = true;
            await db.SaveChangesAsync();
        }
        return version;
    }

    private static (CustomerDocumentStorageAdapter Adapter, Mock<IObjectStorage> Objects, Mock<IProtectedDocumentGenerationReader> Reader) Storage(CustomerDocumentVersion version, string? failure = null)
    {
        var objects = new Mock<IObjectStorage>(MockBehavior.Strict);
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        var reader = new Mock<IProtectedDocumentGenerationReader>(MockBehavior.Strict);
        StorageMoveEvidence? proof = failure == "absent" ? null : new(failure != "unclean", version.StorageBucket,
            failure == "sourceLocation" ? "customer-documents/another-customer/quarantine" :
                $"customer-documents/{version.CustomerId}/{version.DocumentId:N}/{version.Id:N}/quarantine/{version.ScanOperationId:N}", failure == "source" ? 104 : 103,
            version.StorageBucket, version.StorageObjectName, failure == "destination" ? 110 : 109,
            failure == "pending" ? "SourceDeleted" : "MetadataCommitted");
        journal.Setup(x => x.FindAsync(version.ScanOperationId, It.IsAny<CancellationToken>())).ReturnsAsync(proof);
        objects.Setup(x => x.GetEvidenceAsync(version.StorageBucket, version.StorageObjectName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObjectEvidence(failure == "replacement" ? 110 : 109, Bytes.Length));
        if (failure == "provider")
            objects.Setup(x => x.GetEvidenceAsync(version.StorageBucket, version.StorageObjectName, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Synthetic provider unavailable."));
        if (failure == "timeout")
            objects.Setup(x => x.GetEvidenceAsync(version.StorageBucket, version.StorageObjectName, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException("Synthetic provider timeout."));
        reader.Setup(x => x.ReadAsync(version.StorageBucket, version.StorageObjectName, 109, CustomerDocumentOptions.MaximumBytes, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReadOnlyMemory<byte>)(failure == "bytes" ? new byte[Bytes.Length] : Bytes));
        var adapter = new CustomerDocumentStorageAdapter(objects.Object, Mock.Of<IFileSafetyScanner>(), journal.Object,
            reader.Object, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = version.StorageBucket }));
        return (adapter, objects, reader);
    }

    private sealed class EvidenceHost(string connection, IProtectedDocumentStorage? storage) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public HttpClient Client()
        {
            var http = CreateClient();
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                [new Claim("sub", "synthetic-authorized-subject"), new Claim("permissions", CustomerDocumentPermissions.Read)],
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return http;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:FileDbContext", connection);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.ConfigureServices(services =>
            {
                services.AddCustomerDocuments(connection, enabled: true);
                if (storage is not null) services.AddSingleton(storage);
                services.AddProtectedCustomerDocuments();
                services.AddProtectedCustomerDocumentContentEvidence();
                services.RemoveAll<ICustomerDocumentAuthority>();
                var authority = new Mock<ICustomerDocumentAuthority>();
                authority.Setup(x => x.AuthorizeAsync(It.IsAny<DocumentActor>(), 23, CustomerDocumentPermissions.Read, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "synthetic-authorized-subject"));
                services.AddSingleton(authority.Object);
                services.RemoveAll<ICustomerDocumentAssociationValidator>();
                var associations = new Mock<ICustomerDocumentAssociationValidator>();
                associations.Setup(x => x.ValidateAsync(It.IsAny<DocumentActor>(), 23, It.IsAny<IReadOnlyList<DocumentAssociation>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(DocumentAuthorityOutcome.Allowed);
                services.AddSingleton(associations.Object);
            });
        }
    }
}
