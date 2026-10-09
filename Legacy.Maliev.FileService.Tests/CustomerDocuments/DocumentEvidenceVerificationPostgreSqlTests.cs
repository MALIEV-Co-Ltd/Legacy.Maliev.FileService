using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class DocumentEvidenceVerificationPostgreSqlTests(CustomerDocumentPostgreSqlFixture fixture)
{
    private static readonly DocumentActor Actor = new(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "synthetic-session")], "synthetic")));

    [Theory]
    [InlineData(DocumentKind.Shipment)]
    [InlineData(DocumentKind.Release)]
    [InlineData(DocumentKind.Acceptance)]
    [InlineData(DocumentKind.BillingInstruction)]
    public async Task StaffVerificationAppendsExactRevisionAndAuditWithoutRewritingEvidence(DocumentKind kind)
    {
        var ids = await fixture.SeedAsync(kind: kind);
        await using var db = fixture.CreateContext();
        var before = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == ids.Version);
        var receipt = await Service(db).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "Reviewed exact clean evidence"), default);
        Assert.NotNull(receipt);
        Assert.Equal(VerificationStatus.Verified, receipt.VerificationStatus);
        Assert.Equal(2, receipt.Revision);
        Assert.Equal("canonical-active-employee", receipt.VerifiedBySubject);
        Assert.Equal(TimeSpan.Zero, receipt.VerifiedAtUtc!.Value.Offset);
        var after = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == ids.Version);
        Assert.Equal(before.ContentSha256, after.ContentSha256);
        Assert.Equal(before.StorageGeneration, after.StorageGeneration);
        Assert.Equal(before.Revision, after.Revision);
        Assert.True(after.AssociationsSealed);
        Assert.Equal(2, await db.Associations.CountAsync(x => x.VersionId == ids.Version));
        Assert.Single(await db.Audits.Where(x => x.VersionId == ids.Version && x.Action == "VerificationDecided" && x.Revision == 2).ToListAsync());
    }

    [Fact]
    public async Task StaleRevisionCannotAppendAnotherDecision()
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        await using var db = fixture.CreateContext();
        await Service(db).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "First review"), default);
        await Assert.ThrowsAsync<DocumentVerificationConflictException>(() => Service(db).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Rejected", "Stale review"), default));
        Assert.Single(await db.Verifications.Where(x => x.VersionId == ids.Version).ToListAsync());
    }

    [Theory]
    [InlineData("member")]
    [InlineData("inactive")]
    [InlineData("canonical")]
    public async Task RefusedCurrentProofCannotCreateVerification(string failure)
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<DocumentAuthorityDeniedException>(() => Service(db, failure).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "Synthetic review"), default));
        Assert.Empty(await db.Verifications.Where(x => x.VersionId == ids.Version).ToListAsync());
    }

    [Fact]
    public async Task UnavailableCleanProofCannotCreateVerification()
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Service(db, "clean").VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "Synthetic review"), default));
        Assert.Empty(await db.Verifications.Where(x => x.VersionId == ids.Version).ToListAsync());
    }

    [Fact]
    public async Task NdaMustUseLegalVerificationEndpoint()
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Nda);
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<DocumentVerificationKindException>(() => Service(db).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "Synthetic review"), default));
    }

    [Fact]
    public async Task ConcurrentSelectedRevisionAllowsExactlyOneDecision()
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        async Task<bool> Decide()
        {
            await using var db = fixture.CreateContext();
            try
            {
                await Service(db).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "Concurrent review"), default);
                return true;
            }
            catch (DocumentVerificationConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Decide(), Decide());
        Assert.Single(outcomes, x => x);
        await using var check = fixture.CreateContext();
        Assert.Single(await check.Verifications.Where(x => x.VersionId == ids.Version).ToListAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackVerificationAppend()
    {
        var ids = await fixture.SeedAsync(kind: DocumentKind.Shipment);
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION synthetic_verify_audit_unavailable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action"='VerificationDecided' THEN RAISE EXCEPTION 'synthetic private details' USING ERRCODE='53300'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER synthetic_verify_audit_unavailable BEFORE INSERT ON "CustomerDocumentAudit"
                FOR EACH ROW EXECUTE FUNCTION synthetic_verify_audit_unavailable();
            """);
        try
        {
            await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Service(db).VerifyAsync(Actor, 23, ids.Document, ids.Version, new(1, "Verified", "Synthetic review"), default));
            await using var check = fixture.CreateContext();
            Assert.Empty(await check.Verifications.Where(x => x.VersionId == ids.Version).ToListAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_verify_audit_unavailable ON \"CustomerDocumentAudit\"; DROP FUNCTION synthetic_verify_audit_unavailable();");
        }
    }

    private static DocumentEvidenceVerificationService Service(CustomerDocumentDbContext db, string failure = "")
    {
        var boundary = new Boundary(failure);
        var options = new CustomerDocumentRegistryOptions { Enabled = true };
        var registry = new CustomerDocumentRegistry(db, boundary, boundary, boundary, options, TimeProvider.System);
        return new(db, registry, boundary, boundary, options, TimeProvider.System);
    }

    private sealed class Boundary(string failure) : ICustomerDocumentAuthority, ICustomerDocumentAssociationValidator,
        ICustomerDocumentContentEvidence, IDocumentVerificationEmployeeAuthority
    {
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) =>
            Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed,
                failure == "member" ? DocumentActorKind.Member : DocumentActorKind.Employee, "canonical-active-employee"));
        public Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customerId, IReadOnlyList<DocumentAssociation> associations, CancellationToken token) =>
            Task.FromResult(failure == "canonical" ? DocumentAuthorityOutcome.Denied : DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token) =>
            Task.FromResult(failure == "clean" ? DocumentAuthorityOutcome.Unavailable : DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) =>
            Task.FromResult(failure == "inactive" ? DocumentAuthorityOutcome.Denied : DocumentAuthorityOutcome.Allowed);
    }
}
