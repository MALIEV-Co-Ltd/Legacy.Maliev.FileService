using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Microsoft.IdentityModel.Tokens;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
// Real HTTP/authentication/application/registry/PostgreSQL/storage orchestration; external owner/provider inputs are synthetic.
[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class ProtectedDocumentJourneyHttpTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Fact]
    public async Task CleanUploadReplayThenAuthenticatedAttachmentRetainsImmutableBytes()
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection);
        using var client = host.Client();
        var key = Guid.NewGuid().ToString();
        using var first = await Upload(client, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var json = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Bucket", json); Assert.DoesNotContain("ObjectName", json); Assert.DoesNotContain("Uri", json);
        var receipt = JsonSerializer.Deserialize<DocumentVersionReceipt>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var replay = await Upload(client, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(json, await replay.Content.ReadAsStringAsync());
        Assert.Single(await db.Versions.Where(x => x.DocumentId == receipt.DocumentId).ToArrayAsync());
        var version = await db.Versions.SingleAsync(x => x.Id == receipt.VersionId);
        Assert.True(version.AssociationsSealed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), version.ContentSha256);
        using var download = await client.GetAsync($"/customers/23/documents/{receipt.DocumentId}/versions/{receipt.VersionId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        var document = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == receipt.DocumentId);
        Assert.Equal(version.Revision, (await db.Audits.SingleAsync(x => x.DocumentId == document.Id && x.Action == "Download")).Revision);
        using var wrong = await client.GetAsync($"/customers/24/documents/{receipt.DocumentId}/versions/{receipt.VersionId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
    }
    [Fact]
    public async Task ChangedPrivateBytesAreRefusedBeforeAttachment()
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection);
        using var client = host.Client();
        using var upload = await Upload(client, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var receipt = await upload.Content.ReadFromJsonAsync<DocumentVersionReceipt>();
        host.SubstituteBytes();
        using var response = await client.GetAsync($"/customers/23/documents/{receipt!.DocumentId}/versions/{receipt.VersionId}/download");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.DoesNotContain("%PDF", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task DownloadAuditCapturesAuthorizedVerificationReceiptRevision()
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); using var client = host.Client();
        using var upload = await Upload(client, Guid.NewGuid().ToString()); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var receipt = (await upload.Content.ReadFromJsonAsync<DocumentVersionReceipt>())!;
        db.Verifications.Add(new DocumentVerificationEvidence { VersionId = receipt.VersionId, Revision = 7, Status = VerificationStatus.Verified, VerifiedBySubject = "synthetic-verifier", VerifiedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        using var download = await client.GetAsync($"/customers/23/documents/{receipt.DocumentId}/versions/{receipt.VersionId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var authorized = await db.Audits.SingleAsync(x => x.VersionId == receipt.VersionId && x.Action == "ReceiptRead");
        Assert.Equal(7, authorized.Revision);
        Assert.Equal(authorized.Revision, (await db.Audits.SingleAsync(x => x.VersionId == receipt.VersionId && x.Action == "Download")).Revision);
    }
    [Fact]
    public async Task CompletedReplayRepairsOnlyExactCleanSourceDeletedMetadataAcknowledgement()
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); host.LoseNextMetadataAcknowledgement(); using var client = host.Client();
        var key = Guid.NewGuid().ToString();
        using var first = await Upload(client, key); Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        var checkpoint = await db.Set<ProtectedDocumentUploadCheckpoint>().SingleAsync(x => x.OperationId == host.LastOperation);
        Assert.Equal("Completed", checkpoint.State);
        Assert.Equal("SourceDeleted", host.LastProof.State);
        using var replay = await Upload(client, key); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var receipt = (await replay.Content.ReadFromJsonAsync<DocumentVersionReceipt>())!;
        Assert.Equal(checkpoint.VersionId, receipt.VersionId);
        Assert.Equal("MetadataCommitted", host.LastProof.State);
        Assert.Equal(1, host.UploadCount); Assert.Equal(1, host.MoveCount);
        Assert.Single(await db.Versions.Where(x => x.DocumentId == receipt.DocumentId).ToArrayAsync());
        using var download = await client.GetAsync($"/customers/23/documents/{receipt.DocumentId}/versions/{receipt.VersionId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal(Bytes, await download.Content.ReadAsByteArrayAsync());
    }
    [Fact]
    public async Task CompletedReplayWithRevokedCleanProofCannotReturnMetadata()
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); using var client = host.Client();
        var key = Guid.NewGuid().ToString(); using var upload = await Upload(client, key); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        host.CorruptProof("unclean");
        using var replay = await Upload(client, key); Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
        Assert.DoesNotContain("versionId", await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, host.UploadCount); Assert.Equal(1, host.MoveCount);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("unclean")]
    [InlineData("sourceGeneration")]
    [InlineData("destinationGeneration")]
    [InlineData("sourceLocation")]
    [InlineData("destinationLocation")]
    [InlineData("bytes")]
    public async Task CompletedReplayNeverRepairsUnconfirmedSourceDeletedEvidence(string failure)
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); host.LoseNextMetadataAcknowledgement(); using var client = host.Client();
        var key = Guid.NewGuid().ToString(); using var first = await Upload(client, key); Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        if (failure == "bytes") host.SubstituteBytes(); else host.CorruptProof(failure);
        using var replay = await Upload(client, key); Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
        Assert.NotEqual("MetadataCommitted", host.LastProof.State);
        Assert.Equal(1, host.MetadataAcknowledgements); Assert.Equal(1, host.UploadCount); Assert.Equal(1, host.MoveCount);
        Assert.Single(await db.Versions.Where(x => x.ScanOperationId == host.LastOperation).ToArrayAsync());
    }
    [Fact]
    public async Task MaximumPersistableTitleIsAccepted()
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); using var client = host.Client();
        using var response = await Upload(client, Guid.NewGuid().ToString(), title: new string('t', 250));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    [Theory]
    [InlineData(251)]
    [InlineData(256)]
    [InlineData(257)]
    public async Task TitleBeyondDatabaseLimitIsRejectedBeforeStorage(int length)
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); using var client = host.Client();
        using var response = await Upload(client, Guid.NewGuid().ToString(), title: new string('t', length));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.UploadCount); Assert.Equal(0, host.MoveCount);
    }
    [Fact]
    public async Task MemberCannotArchiveInternalDocumentEvenWithCustomerArchivePermission()
    {
        var identity = await fixture.SeedAsync(DocumentVisibility.Internal);
        await using var db = fixture.CreateContext();
        var before = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        await using var host = new Factory(fixture.Connection, DocumentActorKind.Member); using var client = host.Client(canArchive: true);
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{identity.Document}/archive", new { ExpectedRevision = before.Revision, Reason = "Synthetic member attempt" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        Assert.Null(after.ArchivedAtUtc); Assert.Equal(before.Revision, after.Revision);
        Assert.False(await db.Audits.AnyAsync(x => x.DocumentId == identity.Document && x.Action == "Archive"));
        Assert.True(await db.Versions.AnyAsync(x => x.Id == identity.Version));
    }
    [Theory]
    [InlineData(DocumentActorKind.Member, DocumentVisibility.Customer)]
    [InlineData(DocumentActorKind.Employee, DocumentVisibility.Internal)]
    public async Task AuthorizedVisibleDocumentArchiveRetainsPermittedManagement(DocumentActorKind actorKind, DocumentVisibility visibility)
    {
        var identity = await fixture.SeedAsync(visibility);
        await using var db = fixture.CreateContext();
        var before = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        await using var host = new Factory(fixture.Connection, actorKind); using var client = host.Client(canArchive: true);
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{identity.Document}/archive", new { ExpectedRevision = before.Revision, Reason = "Synthetic authorized management" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        Assert.NotNull(after.ArchivedAtUtc); Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Single(await db.Audits.Where(x => x.DocumentId == identity.Document && x.Action == "Archive").ToArrayAsync());
        Assert.True(await db.Versions.AnyAsync(x => x.Id == identity.Version));
    }
    [Fact]
    public async Task UndefinedActorCannotArchiveVisibleDocument()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        var before = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        await using var host = new Factory(fixture.Connection, (DocumentActorKind)99); using var client = host.Client(canArchive: true);
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{identity.Document}/archive", new { ExpectedRevision = before.Revision, Reason = "Synthetic malformed authority" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var after = await db.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        Assert.Null(after.ArchivedAtUtc); Assert.Equal(before.Revision, after.Revision);
        Assert.False(await db.Audits.AnyAsync(x => x.DocumentId == identity.Document && x.Action == "Archive"));
    }
    [Fact]
    public async Task NamedAssociationKindIsCapturedOnImmutableVersion()
    {
        await using var db = fixture.CreateContext(); await db.Database.MigrateAsync();
        await using var host = new Factory(fixture.Connection); using var client = host.Client();
        using var response = await Upload(client, Guid.NewGuid().ToString(), "[{\"Kind\":\"Order\",\"ResourceId\":81}]");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<DocumentVersionReceipt>();
        var association = await db.Associations.SingleAsync(x => x.VersionId == receipt!.VersionId);
        Assert.Equal(DocumentResourceKind.Order, association.Kind); Assert.Equal(81, association.ResourceId); Assert.Equal(23, association.CustomerId);
    }
    private static readonly byte[] Bytes = "%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF\n"u8.ToArray();
    private static Task<HttpResponseMessage> Upload(HttpClient client, string key, string associations = "[]", string title = "Synthetic NDA")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "files", "synthetic.pdf"); content.Add(new StringContent("Nda"), "Kind"); content.Add(new StringContent(title), "Title"); content.Add(new StringContent("Customer"), "Visibility"); content.Add(new StringContent(associations), "Associations");
        var request = new HttpRequestMessage(HttpMethod.Post, "/customers/23/documents") { Content = content }; request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }
    private sealed class Factory(string connection, DocumentActorKind actorKind = DocumentActorKind.Employee) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        private readonly Dictionary<(string Bucket, string Name), (long Generation, byte[] Bytes)> objects = [];
        private readonly Dictionary<Guid, StorageMoveEvidence> proofs = [];
        private long generation = 70;
        private bool loseMetadataAcknowledgement;
        public int UploadCount { get; private set; }
        public int MoveCount { get; private set; }
        public int MetadataAcknowledgements { get; private set; }
        public Guid LastOperation => proofs.Keys.Single();
        public StorageMoveEvidence LastProof => proofs[LastOperation];
        public void LoseNextMetadataAcknowledgement() => loseMetadataAcknowledgement = true;
        public void CorruptProof(string failure)
        {
            proofs[LastOperation] = failure switch
            {
                "unknown" => LastProof with { State = "Unknown" },
                "unclean" => LastProof with { ScanClean = false },
                "sourceGeneration" => LastProof with { SourceGeneration = LastProof.SourceGeneration + 1 },
                "destinationGeneration" => LastProof with { DestinationGeneration = LastProof.DestinationGeneration + 1 },
                "sourceLocation" => LastProof with { SourceObjectName = "customer-documents/24/foreign/quarantine" },
                "destinationLocation" => LastProof with { DestinationObjectName = "customer-documents/24/foreign/original" },
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            };
        }
        public void SubstituteBytes()
        {
            foreach (var location in objects.Keys.ToArray()) { var value = objects[location]; var bytes = value.Bytes.ToArray(); bytes[10] ^= 1; objects[location] = (value.Generation, bytes); }
        }
        public HttpClient Client(bool canArchive = false)
        {
            var client = CreateClient(); var now = DateTime.UtcNow;
            var claims = new List<Claim> { new("sub", "synthetic"), new("permissions", CustomerDocumentPermissions.Read), new("permissions", CustomerDocumentPermissions.Write) };
            if (canArchive) claims.Add(new("permissions", CustomerDocumentPermissions.Archive));
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid", claims, now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt)); return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.UseSetting("ConnectionStrings:FileDbContext", "Host=127.0.0.1;Database=synthetic;Username=synthetic;Password=synthetic"); builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()))); builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid"); builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.ConfigureServices(services =>
            {
                services.AddCustomerDocuments(connection, enabled: true); services.AddProtectedCustomerDocuments();
                services.AddSingleton<Microsoft.Extensions.Options.IOptions<CustomerDocumentOptions>>(Microsoft.Extensions.Options.Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic-private" }));
                var authority = new Mock<ICustomerDocumentAuthority>();
                authority.Setup(x => x.AuthorizeAsync(It.IsAny<DocumentActor>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((DocumentActor actor, int customer, string permission, CancellationToken token) => Task.FromResult(new DocumentAuthorityDecision(customer == 23 ? DocumentAuthorityOutcome.Allowed : DocumentAuthorityOutcome.Denied, actorKind, "synthetic")));
                services.RemoveAll<ICustomerDocumentAuthority>(); services.AddSingleton(authority.Object);
                var associations = new Mock<ICustomerDocumentAssociationValidator>(); associations.Setup(x => x.ValidateAsync(It.IsAny<DocumentActor>(), 23, It.IsAny<IReadOnlyList<DocumentAssociation>>(), It.IsAny<CancellationToken>())).ReturnsAsync(DocumentAuthorityOutcome.Allowed);
                services.RemoveAll<ICustomerDocumentAssociationValidator>(); services.AddSingleton(associations.Object);
                services.RemoveAll<ICustomerDocumentContentEvidence>(); services.AddScoped<ICustomerDocumentContentEvidence, CustomerDocumentContentEvidenceClient>();
                var protection = new Mock<IDocumentProtectionService>(); protection.Setup(x => x.EvaluateAsync(It.IsAny<DocumentActor>(), 23, It.Is<ProtectionRequest>(r => r.Action == ProtectionAction.Read && r.VersionId != null), It.IsAny<CancellationToken>())).ReturnsAsync(new ProtectionDecision(true, false, default, "SyntheticScopedRead")); services.AddSingleton(protection.Object);
                var provider = new Mock<IObjectStorage>(MockBehavior.Strict);
                provider.Setup(x => x.UploadGenerationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (string bucket, string name, string type, Stream bytes, CancellationToken token) => { UploadCount++; using var output = new MemoryStream(); await bytes.CopyToAsync(output, token); var gen = ++generation; objects.Add((bucket, name), (gen, output.ToArray())); return gen; });
                provider.Setup(x => x.GetEvidenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string bucket, string name, CancellationToken token) => Task.FromResult(objects.TryGetValue((bucket, name), out var value) ? new StorageObjectEvidence(value.Generation, value.Bytes.Length) : null));
                provider.Setup(x => x.MoveJournaledAsync(It.IsAny<Guid>(), It.IsAny<long?>(), true, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((Guid id, long? sourceGen, bool clean, string sourceBucket, string sourceName, string destinationBucket, string destinationName, CancellationToken token) => { MoveCount++; var source = objects[(sourceBucket, sourceName)]; if (source.Generation != sourceGen) return Task.FromResult(false); var dest = ++generation; objects.Add((destinationBucket, destinationName), (dest, source.Bytes)); objects.Remove((sourceBucket, sourceName)); proofs.Add(id, new(true, sourceBucket, sourceName, source.Generation, destinationBucket, destinationName, dest, "SourceDeleted")); return Task.FromResult(true); });
                services.RemoveAll<IObjectStorage>(); services.AddSingleton(provider.Object);
                var scanner = new Mock<IFileSafetyScanner>(); scanner.Setup(x => x.ScanAsync(It.IsAny<IUploadFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new FileSafetyResult(FileSafetyVerdict.Clean)); services.RemoveAll<IFileSafetyScanner>(); services.AddSingleton(scanner.Object);
                var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict); journal.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns((Guid id, CancellationToken token) => Task.FromResult(proofs.GetValueOrDefault(id))); journal.Setup(x => x.MetadataCommittedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns((Guid id, CancellationToken token) => { MetadataAcknowledgements++; if (loseMetadataAcknowledgement) { loseMetadataAcknowledgement = false; throw new IOException("Synthetic lost metadata acknowledgement"); } proofs[id] = proofs[id] with { State = "MetadataCommitted" }; return Task.CompletedTask; }); services.RemoveAll<IStorageMoveJournal>(); services.AddSingleton(journal.Object);
                services.RemoveAll<IQuarantineUploadIntent>(); services.AddSingleton<IQuarantineUploadIntent, SyntheticCustody>();
                var reader = new Mock<IProtectedDocumentGenerationReader>(); reader.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns((string bucket, string name, long gen, long maximum, CancellationToken token) => { var value = objects[(bucket, name)]; if (value.Generation != gen || value.Bytes.Length > maximum) throw new DocumentAuthorityUnavailableException(); return Task.FromResult<ReadOnlyMemory<byte>>(value.Bytes.ToArray()); }); services.RemoveAll<IProtectedDocumentGenerationReader>(); services.AddSingleton(reader.Object);
            });
        }
        private sealed class SyntheticCustody : IQuarantineUploadIntent
        {
            public Task PrepareAsync(Guid operationId, Guid parentOperationId, string bucket, string objectName, string contentType, long declaredSize, CancellationToken token) => Task.CompletedTask;
            public Task AcknowledgeAsync(Guid operationId, long generation, CancellationToken token) => Task.CompletedTask;
            public Task UnknownAsync(Guid operationId, CancellationToken token) => Task.CompletedTask;
        }
    }
}
