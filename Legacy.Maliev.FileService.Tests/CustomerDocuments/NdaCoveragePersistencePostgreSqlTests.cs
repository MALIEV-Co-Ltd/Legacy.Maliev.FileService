using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class NdaCoveragePersistencePostgreSqlTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Theory]
    [InlineData(DocumentResourceKind.Quotation)]
    [InlineData(DocumentResourceKind.Replacement)]
    public async Task VerifiedRequestedScopeProtectsCanonicalOriginalOrder(DocumentResourceKind kind)
    {
        var identity = await fixture.SeedAsync();
        var orderId = Random.Shared.Next(100000, 2000000000);
        var resourceId = Random.Shared.Next(100000, 2000000000);
        await using var db = fixture.CreateContext();
        var owners = new CoverageOwners(resourceId, orderId);
        var authority = new NdaOwnerAuthority(new SyntheticAuthority(DocumentActorKind.Employee),
            new NdaCanonicalCoverageResolver(db, owners, owners, owners), owners);
        var options = new NdaOptions { Enabled = true };
        var repository = new NdaRepository(db);
        var verification = new NdaVerificationService(authority, repository, new SyntheticEvidence(), options, TimeProvider.System);
        var actor = new DocumentActor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "employee")], "test")));
        await verification.VerifyAsync(actor, 23, identity.Document, Request(identity.Version, new(kind, resourceId)), default);
        var decision = await new DocumentProtectionService(authority, repository, options, TimeProvider.System)
            .EvaluateAsync(actor, 23, new(DocumentResourceKind.Order, orderId, ProtectionAction.PublishSocial, true), default);
        Assert.False(decision.Allowed);
        Assert.True(decision.Protected);
    }
    [Fact]
    public async Task SignedVersionUnrelatedAssociationsAreNotCertifiedAsLegalCoverage()
    {
        var identity = await fixture.SeedAsync();
        var orderId = Random.Shared.Next(100000, 2000000000);
        await using var db = fixture.CreateContext();
        var owners = new CoverageOwners(999999, orderId);
        var authority = new NdaOwnerAuthority(new SyntheticAuthority(DocumentActorKind.Employee),
            new NdaCanonicalCoverageResolver(db, owners, owners, owners), owners);
        var receipt = await new NdaVerificationService(authority, new NdaRepository(db), new SyntheticEvidence(), new NdaOptions { Enabled = true }, TimeProvider.System)
            .VerifyAsync(new(new ClaimsPrincipal(new ClaimsIdentity([], "test"))), 23, identity.Document, Request(identity.Version, new(DocumentResourceKind.Order, orderId)), default);
        var coverage = await db.Set<NdaCoverage>().Where(x => x.NdaId == receipt.NdaId).ToListAsync();
        Assert.Single(coverage);
        Assert.Equal(orderId, coverage[0].ResourceId);
        Assert.Equal(DocumentResourceKind.Order, coverage[0].Kind);
    }
    private static NdaVerificationRequest Request(Guid version, NdaCoverageRequest coverage) => new(version, 1, "Synthetic A", "Synthetic B",
        DateTimeOffset.UtcNow.AddDays(-1), null, null, NdaSurvivalKind.Indefinite, null, "responsible", [coverage], "Reviewed explicit synthetic scope");
    private sealed class CoverageOwners(int resourceId, int originalOrderId) : ICustomerDocumentOwnerReads, IReplacementCaseLineageReader, INdaOrderConsentReader, INdaEmployeeAuthority
    {
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int customerId, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentCustomer>(DocumentAuthorityOutcome.Allowed, new(customerId)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int orderId, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentOrder>(DocumentAuthorityOutcome.Allowed, new(orderId, 23)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int quotationId, int customerId, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentQuotation>(DocumentAuthorityOutcome.Allowed, new(quotationId, customerId)));
        public Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int quotationId, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>(DocumentAuthorityOutcome.Allowed, [new(1, quotationId, quotationId == resourceId ? originalOrderId : 81)]));
        Task<CustomerDocumentOwnerRead<ReplacementCaseLineage>> IReplacementCaseLineageReader.ReadAsync(int caseId, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<ReplacementCaseLineage>(DocumentAuthorityOutcome.Allowed, new(caseId, 23, [originalOrderId], [], [], 1)));
        Task<CustomerDocumentOwnerRead<bool>> INdaOrderConsentReader.ReadAsync(int orderId, int customerId, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<bool>(DocumentAuthorityOutcome.Allowed, true));
        public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token) => Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "employee"));
    }
}
