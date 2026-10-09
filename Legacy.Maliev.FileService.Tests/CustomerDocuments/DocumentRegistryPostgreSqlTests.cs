using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using System.Security.Claims;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[CollectionDefinition(Name)]
public sealed class CustomerDocumentPostgreSqlCollection : ICollectionFixture<CustomerDocumentPostgreSqlFixture>
{
    public const string Name = "CustomerDocumentPostgreSQL";
}
public sealed class CustomerDocumentPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public string Connection => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public async Task DisposeAsync() => await container.DisposeAsync();
    public CustomerDocumentDbContext CreateContext() => new(new DbContextOptionsBuilder<CustomerDocumentDbContext>()
        .UseNpgsql(Connection, x => x.MigrationsHistoryTable("__CustomerDocumentMigrationsHistory")).Options);
    public async Task<(Guid Document, Guid Version)> SeedAsync(DocumentVisibility visibility = DocumentVisibility.Customer, DocumentKind kind = DocumentKind.Nda)
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        var document = new CustomerDocument { Id = Guid.NewGuid(), CustomerId = 23, Kind = kind, Title = "Synthetic NDA", Visibility = visibility };
        var version = new CustomerDocumentVersion
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            CustomerId = 23,
            Kind = kind,
            VersionNumber = 1,
            ContentSha256 = new string('a', 64),
            ActorSubject = "synthetic-uploader",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StorageBucket = "synthetic-private",
            StorageObjectName = "customer-documents/synthetic.pdf",
            StorageGeneration = 1,
            ContentSize = 5,
            ContentType = "application/pdf",
            OriginalFileName = "synthetic.pdf",
            ScanOperationId = Guid.NewGuid(),
            ScanSourceGeneration = 1
        };
        version.StorageObjectName = $"customer-documents/23/{document.Id:N}/{version.Id:N}/original";
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        var boundary = new SyntheticBoundary();
        var registry = new CustomerDocumentRegistry(db, boundary, boundary, boundary, new() { Enabled = true }, TimeProvider.System);
        await registry.FinalizeVersionAsync(new(new ClaimsPrincipal()), version,
            [new DocumentAssociation { VersionId = version.Id, CustomerId = 23, Kind = DocumentResourceKind.Quotation, ResourceId = 67 },
             new DocumentAssociation { VersionId = version.Id, CustomerId = 23, Kind = DocumentResourceKind.Order, ResourceId = 81 }], default);
        return (document.Id, version.Id);
    }

    // Only the pending owner boundaries are controlled; persistence and sealing use the actual registry.
    private sealed class SyntheticBoundary : ICustomerDocumentAuthority, ICustomerDocumentAssociationValidator, ICustomerDocumentContentEvidence
    {
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) =>
            Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "synthetic-uploader"));
        public Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customerId, IReadOnlyList<DocumentAssociation> links, CancellationToken token) =>
            Task.FromResult(DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string digest, CancellationToken token) =>
            Task.FromResult(DocumentAuthorityOutcome.Allowed);
    }
}
[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class DocumentRegistryPostgreSqlTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Fact]
    public async Task FreshMigration_CreatesSeparateRegistryAndHistoryWithoutLegacyUpload()
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT to_regclass('\"CustomerDocument\"') IS NOT NULL AND to_regclass('\"Upload\"') IS NULL AND to_regclass('\"__CustomerDocumentMigrationsHistory\"') IS NOT NULL";
        Assert.Equal(true, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task TenantForeignKey_RejectsVersionForDifferentCustomer()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        db.Versions.Add(Version(identity.Document, 24, 2));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Fact]
    public async Task HashConstraint_RejectsNoncanonicalDigest()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        var version = Version(identity.Document, 23, 2);
        version.ContentSha256 = new string('A', 64);
        db.Versions.Add(version);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Fact]
    public async Task ConcurrentVersionNumber_ExactlyOneInsertCommits()
    {
        var identity = await fixture.SeedAsync();
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        first.Versions.Add(Version(identity.Document, 23, 2));
        second.Versions.Add(Version(identity.Document, 23, 2));
        async Task<bool> Save(CustomerDocumentDbContext db)
        {
            try { await db.SaveChangesAsync(); return true; }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { return false; }
        }
        var outcomes = await Task.WhenAll(Save(first), Save(second));
        Assert.Single(outcomes, x => x);
    }
    [Fact]
    public async Task ImmutableVersion_RejectsTrackedAndDirectSqlRewrite()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        var version = await db.Versions.SingleAsync(x => x.Id == identity.Version);
        version.ContentSha256 = new string('c', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"CustomerDocumentVersion\" SET \"ActorSubject\" = 'changed' WHERE \"Id\" = {identity.Version}"));
        await using var fresh = fixture.CreateContext();
        Assert.Equal("synthetic-uploader", (await fresh.Versions.SingleAsync(x => x.Id == identity.Version)).ActorSubject);
    }
    [Fact]
    public async Task AssociationTenantForeignKey_RejectsCrossCustomerReference()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        db.Associations.Add(new DocumentAssociation { VersionId = identity.Version, Kind = DocumentResourceKind.Order, ResourceId = 99, CustomerId = 24 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    private static CustomerDocumentVersion Version(Guid document, int customer, int number)
    {
        var id = Guid.NewGuid();
        return new()
        {
            Id = id,
            DocumentId = document,
            CustomerId = customer,
            Kind = DocumentKind.Nda,
            VersionNumber = number,
            ContentSha256 = new string('b', 64),
            ActorSubject = "synthetic-uploader",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StorageBucket = "synthetic-private",
            StorageObjectName = $"customer-documents/{customer}/{document:N}/{id:N}/original",
            StorageGeneration = 1,
            ContentSize = 5,
            ContentType = "application/pdf",
            OriginalFileName = "synthetic.pdf",
            ScanOperationId = Guid.NewGuid(),
            ScanSourceGeneration = 1
        };
    }
    [Fact]
    public async Task ParentDocumentKind_RejectsMisclassifiedVersion()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        var version = Version(identity.Document, 23, 2);
        version.Kind = DocumentKind.BillingInstruction;
        db.Versions.Add(version);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task FinalizedAssociationSet_RejectsAdditionalInsertion()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        db.Associations.Add(new() { VersionId = identity.Version, CustomerId = 23, Kind = DocumentResourceKind.Order, ResourceId = 99 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Seal_CannotBeReversedOrCombinedWithDigestRewrite()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"CustomerDocumentVersion\" SET \"AssociationsSealed\" = false WHERE \"Id\" = {identity.Version}"));
        var unsealed = Version(identity.Document, 23, 2);
        db.Versions.Add(unsealed);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"CustomerDocumentVersion\" SET \"AssociationsSealed\" = true, \"ContentSha256\" = {new string('d', 64)} WHERE \"Id\" = {unsealed.Id}"));
    }

    [Fact]
    public async Task AssociationInsertAndSeal_SerializeOnSameVersionRow()
    {
        var identity = await fixture.SeedAsync();
        await using var seed = fixture.CreateContext();
        var unsealed = Version(identity.Document, 23, 2);
        seed.Versions.Add(unsealed);
        await seed.SaveChangesAsync();
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var inserting = await first.Database.BeginTransactionAsync();
        await first.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"CustomerDocumentAssociation\" (\"VersionId\",\"Kind\",\"ResourceId\",\"CustomerId\") VALUES ({unsealed.Id},1,99,23)");
        await using var sealing = await second.Database.BeginTransactionAsync();
        var seal = second.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"CustomerDocumentVersion\" SET \"AssociationsSealed\" = true WHERE \"Id\" = {unsealed.Id}");
        using var observer = new NpgsqlConnection(fixture.Connection);
        await observer.OpenAsync();
        var blocked = false;
        for (var attempt = 0; attempt < 100 && !blocked; attempt++)
        {
            await using var command = observer.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE 'UPDATE%CustomerDocumentVersion%')";
            blocked = (bool)(await command.ExecuteScalarAsync())!;
            if (!blocked) await Task.Yield();
        }
        Assert.True(blocked, "Seal must wait for the association insertion transaction.");
        await inserting.CommitAsync();
        await seal;
        await sealing.CommitAsync();
        await Assert.ThrowsAsync<PostgresException>(() => seed.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"CustomerDocumentAssociation\" (\"VersionId\",\"Kind\",\"ResourceId\",\"CustomerId\") VALUES ({unsealed.Id},1,100,23)"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("synthetic-verifier", false)]
    public async Task VerifiedEvidence_RequiresNonblankActorAndUtcTime(string? actor, bool hasTime)
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        db.Verifications.Add(new()
        {
            VersionId = identity.Version,
            Revision = 2,
            Status = VerificationStatus.Verified,
            VerifiedBySubject = actor,
            VerifiedAtUtc = hasTime ? DateTimeOffset.UtcNow : null
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditLineage_RejectsVersionOfDifferentDocument()
    {
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        db.Audits.Add(new()
        {
            Id = Guid.NewGuid(),
            DocumentId = first.Document,
            VersionId = second.Version,
            ActorSubject = "synthetic-auditor",
            AtUtc = DateTimeOffset.UtcNow,
            Action = "ReceiptRead",
            Revision = 1
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
