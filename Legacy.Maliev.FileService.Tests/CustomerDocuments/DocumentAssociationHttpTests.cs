using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class DocumentAssociationHttpTests(CustomerDocumentPostgreSqlFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"CustomerDocument\" CASCADE");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GenericStaffVerificationReturnsNamedExactReceiptAndRejectsStaleSelection()
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        var route = $"/customers/23/documents/{ids.Document}/versions/{ids.Version}/verification";
        using var response = await client.PostAsJsonAsync(route, new DocumentEvidenceVerificationRequest(1, "Verified", "Reviewed exact synthetic evidence"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Verified", json.RootElement.GetProperty("VerificationStatus").GetString());
        Assert.Equal("Shipment", json.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(ids.Version, json.RootElement.GetProperty("VersionId").GetGuid());
        Assert.Equal("synthetic-authorized-subject", json.RootElement.GetProperty("VerifiedBySubject").GetString());
        using var stale = await client.PostAsJsonAsync(route, new DocumentEvidenceVerificationRequest(1, "Rejected", "Stale review"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var exact = await client.GetAsync(Route(23, ids));
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var readback = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
        Assert.Equal(2, readback.RootElement.GetProperty("Revision").GetInt64());
    }

    [Theory]
    [InlineData(DocumentActorKind.Member, DocumentAuthorityOutcome.Allowed, true, HttpStatusCode.Forbidden)]
    [InlineData(DocumentActorKind.Employee, DocumentAuthorityOutcome.Unavailable, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DocumentActorKind.Employee, DocumentAuthorityOutcome.Denied, true, HttpStatusCode.Forbidden)]
    [InlineData(DocumentActorKind.Employee, DocumentAuthorityOutcome.Allowed, false, HttpStatusCode.ServiceUnavailable)]
    public async Task GenericVerificationRefusesMemberInactiveOrUnavailableCleanEvidence(DocumentActorKind actorKind,
        DocumentAuthorityOutcome employment, bool clean, HttpStatusCode expected)
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        await using var factory = new ControlledFactory(fixture.Connection, actorKind, cleanEvidence: clean, employment: employment);
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{ids.Document}/versions/{ids.Version}/verification",
            new DocumentEvidenceVerificationRequest(1, "Verified", "Synthetic review"));
        Assert.Equal(expected, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Empty(await db.Verifications.Where(x => x.VersionId == ids.Version).ToListAsync());
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("PendingVerification")]
    [InlineData("1")]
    public async Task GenericVerificationAcceptsOnlyExactDecisionNames(string status)
    {
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync($"/customers/23/documents/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/verification",
            new DocumentEvidenceVerificationRequest(1, status, "Synthetic review"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GenericVerificationRefusesNumericStatusAndNdaLegalBypass()
    {
        var ids = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        var route = $"/customers/23/documents/{ids.Document}/versions/{ids.Version}/verification";
        using var numeric = await client.PostAsJsonAsync(route, new { ExpectedVerificationRevision = 1, Status = 1, Reason = "Synthetic review" });
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        using var nda = await client.PostAsJsonAsync(route, new DocumentEvidenceVerificationRequest(1, "Verified", "Synthetic review"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nda.StatusCode);
    }
    [Fact]
    public async Task UnsealedVersion_IsUnavailableAndOmittedFromHistory()
    {
        var identity = await fixture.SeedAsync();
        Guid unsealed;
        await using (var db = fixture.CreateContext())
        {
            var source = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == identity.Version);
            unsealed = Guid.NewGuid();
            source.Id = unsealed;
            source.VersionNumber = 2;
            source.StorageObjectName = $"customer-documents/23/{identity.Document:N}/{unsealed:N}/original";
            source.AssociationsSealed = false;
            db.Versions.Add(source);
            await db.SaveChangesAsync();
        }
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var receipt = await client.GetAsync(Route(23, (identity.Document, unsealed)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, receipt.StatusCode);
        using var history = await client.GetAsync($"/customers/23/documents/{identity.Document}/versions");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var json = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.DoesNotContain(json.RootElement.EnumerateArray(), x => x.GetProperty("VersionId").GetGuid() == unsealed);
    }
    [Theory]
    [InlineData(DocumentKind.Nda, "Nda")]
    [InlineData(DocumentKind.Corporate, "Corporate")]
    [InlineData(DocumentKind.BillingInstruction, "BillingInstruction")]
    [InlineData(DocumentKind.Shipment, "Shipment")]
    [InlineData(DocumentKind.Release, "Release")]
    [InlineData(DocumentKind.Acceptance, "Acceptance")]
    [InlineData(DocumentKind.Evidence, "Evidence")]
    public async Task ReceiptKinds_HaveFrozenNamedWireValues(DocumentKind kind, string wire)
    {
        var identity = await fixture.SeedAsync(kind: kind);
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(wire, json.RootElement.GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task MemberList_OmitsInternalMetadataAndHistory()
    {
        var visible = await fixture.SeedAsync();
        var hidden = await fixture.SeedAsync(DocumentVisibility.Internal);
        await using var factory = new ControlledFactory(fixture.Connection, DocumentActorKind.Member);
        using var client = factory.Client();
        using var list = await client.GetAsync("/customers/23/documents?limit=50");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Contains(json.RootElement.EnumerateArray(), x => x.GetProperty("DocumentId").GetGuid() == visible.Document);
        Assert.DoesNotContain(json.RootElement.EnumerateArray(), x => x.GetProperty("DocumentId").GetGuid() == hidden.Document);
        using var history = await client.GetAsync($"/customers/23/documents/{hidden.Document}/versions");
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
    }

    [Fact]
    public async Task ActiveListOmitsArchivedDocumentButRetainsExactReceipt()
    {
        var ids = await fixture.SeedAsync();
        await using (var db = fixture.CreateContext())
            await db.Documents.Where(x => x.Id == ids.Document).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Revision, 2L).SetProperty(x => x.ArchivedAtUtc, DateTimeOffset.UtcNow));
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var list = await client.GetAsync("/customers/23/documents");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.DoesNotContain(json.RootElement.EnumerateArray(), x => x.GetProperty("DocumentId").GetGuid() == ids.Document);
        using var receipt = await client.GetAsync(Route(23, ids));
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
    }

    [Fact]
    public async Task History_ContainsExactCleanVersionMetadataWithoutStorageCoordinates()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var response = await client.GetAsync($"/customers/23/documents/{identity.Document}/versions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var version = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(identity.Version, version.GetProperty("VersionId").GetGuid());
        Assert.Equal(1, version.GetProperty("VersionNumber").GetInt32());
        Assert.Equal("PendingVerification", version.GetProperty("VerificationStatus").GetString());
        Assert.DoesNotContain("StorageBucket", body);
        Assert.DoesNotContain("StorageObjectName", body);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=51")]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    public async Task ListAndHistory_RejectUnboundedPages(string query)
    {
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var list = await client.GetAsync($"/customers/23/documents?{query}");
        using var history = await client.GetAsync($"/customers/23/documents/{Guid.NewGuid()}/versions?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, history.StatusCode);
    }
    [Fact]
    public async Task QueryUnavailable_ReturnsSanitized503()
    {
        var identity = await fixture.SeedAsync();
        var inaccessible = new NpgsqlConnectionStringBuilder(fixture.Connection) { SearchPath = "synthetic_absent_schema" }.ConnectionString;
        await using var factory = new ControlledFactory(fixture.Connection, registryConnection: inaccessible);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("CustomerDocument", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuditInsertUnavailable_ReturnsSanitized503WithoutReceipt()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION synthetic_audit_unavailable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic private provider details' USING ERRCODE='53300'; END; $$;
            CREATE TRIGGER synthetic_audit_unavailable BEFORE INSERT ON "CustomerDocumentAudit"
                FOR EACH ROW EXECUTE FUNCTION synthetic_audit_unavailable();
            """);
        try
        {
            await using var factory = new ControlledFactory(fixture.Connection);
            using var client = factory.Client();
            using var response = await client.GetAsync(Route(23, identity));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic private provider details", body);
            Assert.DoesNotContain(new string('a', 64), body);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER synthetic_audit_unavailable ON "CustomerDocumentAudit";
                DROP FUNCTION synthetic_audit_unavailable();
                """);
        }
    }

    [Fact]
    public async Task UndefinedAuthoritativeActorKind_RefusesReceipt()
    {
        var identity = await fixture.SeedAsync(DocumentVisibility.Internal);
        await using var factory = new ControlledFactory(fixture.Connection, (DocumentActorKind)99);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
    [Fact]
    public async Task SuccessfulReceipt_AppendsExactVersionAuditWithAuthorizedSubject()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = fixture.CreateContext();
        var audit = Assert.Single(await db.Audits.Where(x => x.DocumentId == identity.Document).ToListAsync());
        Assert.Equal(identity.Version, audit.VersionId);
        Assert.Equal("synthetic-authorized-subject", audit.ActorSubject);
        Assert.Equal("ReceiptRead", audit.Action);
        Assert.Equal(1, audit.Revision);
        Assert.Equal(TimeSpan.Zero, audit.AtUtc.Offset);
    }

    [Fact]
    public async Task RegistrationDefaultsDisabled_EvenControlledAuthorityCannotRead()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection, enabled: false);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task MissingExactCleanEvidence_RefusesReceipt()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection, cleanEvidence: false);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ExactReceipt_UsesNamedEnumsAndLatestImmutableVerificationRevision()
    {
        var identity = await fixture.SeedAsync();
        await using (var db = fixture.CreateContext())
        {
            db.Verifications.Add(new()
            {
                VersionId = identity.Version,
                Revision = 2,
                Status = VerificationStatus.Verified,
                VerifiedBySubject = "synthetic-verifier",
                VerifiedAtUtc = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        }
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(identity.Document, root.GetProperty("DocumentId").GetGuid());
        Assert.Equal(identity.Version, root.GetProperty("VersionId").GetGuid());
        Assert.Equal(23, root.GetProperty("CustomerId").GetInt32());
        Assert.Equal("Nda", root.GetProperty("Kind").GetString());
        Assert.Equal("Verified", root.GetProperty("VerificationStatus").GetString());
        Assert.Equal(new string('a', 64), root.GetProperty("ContentSha256").GetString());
        Assert.Equal(67, root.GetProperty("QuotationId").GetInt32());
        Assert.Equal(81, Assert.Single(root.GetProperty("OrderIds").EnumerateArray()).GetInt32());
        Assert.Equal("synthetic-verifier", root.GetProperty("VerifiedBySubject").GetString());
        Assert.Equal(TimeSpan.Zero, root.GetProperty("VerifiedAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal(2, root.GetProperty("Revision").GetInt64());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("Bucket", body);
        Assert.DoesNotContain("ObjectName", body);
        Assert.DoesNotContain("Url", body);
        Assert.DoesNotContain("synthetic-uploader", body);
    }

    [Theory]
    [InlineData(DocumentActorKind.Member, HttpStatusCode.NotFound)]
    [InlineData(DocumentActorKind.Employee, HttpStatusCode.OK)]
    public async Task InternalClassification_EnforcesAuthoritativeIdentityKind(DocumentActorKind kind, HttpStatusCode expected)
    {
        var identity = await fixture.SeedAsync(DocumentVisibility.Internal);
        await using var factory = new ControlledFactory(fixture.Connection, kind);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(expected, response.StatusCode);
        if (kind == DocumentActorKind.Member)
        {
            Assert.Equal(0, factory.AssociationCalls);
            Assert.DoesNotContain("Synthetic NDA", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task CrossCustomerAuthority_DeniesEvenWithValidDocumentAndVersion()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection, DocumentActorKind.Member);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(24, identity));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.AssociationCalls);
    }

    [Fact]
    public async Task WrongDocumentVersionPair_Returns404()
    {
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, (first.Document, second.Version)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, factory.AssociationCalls);
    }

    [Theory]
    [InlineData(DocumentAuthorityOutcome.Denied, HttpStatusCode.Forbidden)]
    [InlineData(DocumentAuthorityOutcome.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task CanonicalQuotationOrOrderNotConfirmed_RefusesReceipt(DocumentAuthorityOutcome outcome, HttpStatusCode expected)
    {
        var identity = await fixture.SeedAsync();
        await using var factory = new ControlledFactory(fixture.Connection, association: outcome);
        using var client = factory.Client();
        using var response = await client.GetAsync(Route(23, identity));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, factory.AssociationCalls);
        Assert.DoesNotContain(new string('a', 64), await response.Content.ReadAsStringAsync());
    }

    private static string Route(int customer, (Guid Document, Guid Version) identity) =>
        $"/customers/{customer}/documents/{identity.Document}/versions/{identity.Version}/receipt";

    // This fixture controls only the pending owner boundary; JWT/permission middleware, controller,
    // registry queries, migrations, classification and receipt JSON are the actual application.
    private sealed class ControlledFactory(string connection, DocumentActorKind kind = DocumentActorKind.Employee,
        DocumentAuthorityOutcome association = DocumentAuthorityOutcome.Allowed, bool enabled = true, bool cleanEvidence = true,
        string? registryConnection = null, DocumentAuthorityOutcome employment = DocumentAuthorityOutcome.Allowed) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public int AssociationCalls { get; private set; }
        public HttpClient Client()
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                [new Claim("sub", "synthetic-actor"), new Claim("permissions", CustomerDocumentPermissions.Read), new Claim("permissions", CustomerDocumentPermissions.Verify)],
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.UseSetting("ConnectionStrings:FileDbContext", connection);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.ConfigureServices(services =>
            {
                services.AddCustomerDocuments(registryConnection ?? connection, enabled);
                services.RemoveAll<ICustomerDocumentAuthority>();
                services.AddSingleton<ICustomerDocumentAuthority>(new ControlledAuthority(kind));
                services.RemoveAll<ICustomerDocumentAssociationValidator>();
                services.AddSingleton<ICustomerDocumentAssociationValidator>(new ControlledAssociations(association, () => AssociationCalls++));
                services.RemoveAll<ICustomerDocumentContentEvidence>();
                services.AddSingleton<ICustomerDocumentContentEvidence>(new ControlledContentEvidence(cleanEvidence));
                services.RemoveAll<IDocumentVerificationEmployeeAuthority>();
                services.AddSingleton<IDocumentVerificationEmployeeAuthority>(new ControlledEmployment(employment));
            });
        }
        private sealed class ControlledEmployment(DocumentAuthorityOutcome outcome) : IDocumentVerificationEmployeeAuthority
        {
            public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) => Task.FromResult(outcome);
        }
        private sealed class ControlledAuthority(DocumentActorKind kind) : ICustomerDocumentAuthority
        {
            public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customer, string permission, CancellationToken token) =>
                Task.FromResult(new DocumentAuthorityDecision(customer == 23 ? DocumentAuthorityOutcome.Allowed : DocumentAuthorityOutcome.Denied, kind, "synthetic-authorized-subject"));
        }
        private sealed class ControlledAssociations(DocumentAuthorityOutcome outcome, Action observed) : ICustomerDocumentAssociationValidator
        {
            public Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customer, IReadOnlyList<DocumentAssociation> links, CancellationToken token)
            {
                observed();
                Assert.Equal(23, customer);
                Assert.Contains(links, x => x.Kind == DocumentResourceKind.Quotation && x.ResourceId == 67);
                Assert.Contains(links, x => x.Kind == DocumentResourceKind.Order && x.ResourceId == 81);
                return Task.FromResult(outcome);
            }
        }
        private sealed class ControlledContentEvidence(bool clean) : ICustomerDocumentContentEvidence
        {
            public Task<DocumentAuthorityOutcome> ValidateAsync(Guid version, string digest, CancellationToken token)
            {
                Assert.NotEqual(Guid.Empty, version);
                Assert.Equal(new string('a', 64), digest);
                return Task.FromResult(clean ? DocumentAuthorityOutcome.Allowed : DocumentAuthorityOutcome.Unavailable);
            }
        }
    }
}
