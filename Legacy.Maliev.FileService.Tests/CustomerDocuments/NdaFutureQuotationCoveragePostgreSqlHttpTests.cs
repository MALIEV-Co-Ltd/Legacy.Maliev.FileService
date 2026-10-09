using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class NdaFutureQuotationCoveragePostgreSqlHttpTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Theory]
    [InlineData(true, false, HttpStatusCode.OK)]
    [InlineData(false, true, HttpStatusCode.OK)]
    [InlineData(true, false, HttpStatusCode.ServiceUnavailable)]
    public async Task LaterQuotationOrderInheritsProtectionWithoutChangingReviewedEvidence(bool laterOrder, bool allowed, HttpStatusCode expectedStatus)
    {
        var customerId = Random.Shared.Next(100000, 1900000000);
        var initialOrder = Random.Shared.Next(100000, 1900000000);
        var quotation = initialOrder + 3;
        var owners = new Owners(customerId, quotation, initialOrder);
        var identity = await Seed(customerId, quotation, initialOrder);
        await using var factory = new NdaHttpFactory(services =>
        {
            services.AddCustomerDocuments(fixture.Connection, enabled: true);
            services.AddCustomerDocumentNda(new NdaOptions { Enabled = true });
            services.AddScoped<ICustomerDocumentOwnerReads>(_ => owners);
            services.AddScoped<INdaOrderConsentReader>(_ => owners);
            services.AddScoped<INdaAuthority>(provider => new NdaOwnerAuthority(new SyntheticAuthority(DocumentActorKind.Employee),
                provider.GetRequiredService<NdaCanonicalCoverageResolver>(), owners));
            services.AddScoped<ICustomerDocumentContentEvidence, SyntheticEvidence>();
        });
        using var verifier = factory.AuthenticatedClient(CustomerDocumentPermissions.Verify);
        using var verification = await verifier.PostAsJsonAsync($"customers/{customerId}/documents/{identity.Document}/nda/verification",
            new NdaVerificationRequest(identity.Version, 1, "Synthetic A", "Synthetic B", DateTimeOffset.UtcNow.AddDays(-1), null, null,
                NdaSurvivalKind.Indefinite, null, "responsible", [new(DocumentResourceKind.Quotation, quotation)], "Reviewed initial quotation scope"));
        Assert.Equal(HttpStatusCode.OK, verification.StatusCode);
        var receipt = (await verification.Content.ReadFromJsonAsync<NdaVerificationReceipt>())!;
        owners.IncludeLaterOrder = true;
        owners.ForwardOutcome = expectedStatus == HttpStatusCode.ServiceUnavailable ? DocumentAuthorityOutcome.Unavailable : DocumentAuthorityOutcome.Allowed;
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.EvaluateProtection);
        using var response = await client.PostAsJsonAsync($"customers/{customerId}/protection/evaluate",
            new ProtectionRequest(DocumentResourceKind.Order, initialOrder + (laterOrder ? 1 : 2), ProtectionAction.PublishSocial, true));
        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.OK)
        {
            var decision = (await response.Content.ReadFromJsonAsync<ProtectionDecision>())!;
            Assert.Equal(allowed, decision.Allowed);
            Assert.Equal(laterOrder, decision.Protected);
        }
        await using var read = fixture.CreateContext();
        var evidence = await read.Set<NdaCoverage>().Where(x => x.NdaId == receipt.NdaId).ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.Contains(evidence, x => x.Kind == DocumentResourceKind.Quotation && x.ResourceId == quotation);
        Assert.Contains(evidence, x => x.Kind == DocumentResourceKind.Order && x.ResourceId == initialOrder);
        Assert.DoesNotContain(evidence, x => x.Kind == DocumentResourceKind.Order && x.ResourceId != initialOrder);
        Assert.True((await read.Set<NdaRecord>().SingleAsync(x => x.Id == receipt.NdaId)).CoverageSealed);
    }

    private async Task<(Guid Document, Guid Version)> Seed(int customerId, int quotation, int order)
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        var document = new CustomerDocument { Id = Guid.NewGuid(), CustomerId = customerId, Kind = DocumentKind.Nda, Title = "Synthetic quotation NDA", Visibility = DocumentVisibility.Customer };
        var version = new CustomerDocumentVersion { Id = Guid.NewGuid(), DocumentId = document.Id, CustomerId = customerId, Kind = document.Kind,
            VersionNumber = 1, ContentSha256 = new string('a', 64), ActorSubject = "synthetic-uploader", CreatedAtUtc = DateTimeOffset.UtcNow,
            StorageBucket = "synthetic-private", StorageGeneration = 1, ContentSize = 5, ContentType = "application/pdf", OriginalFileName = "synthetic.pdf",
            ScanOperationId = Guid.NewGuid(), ScanSourceGeneration = 1 };
        version.StorageObjectName = $"customer-documents/{customerId}/{document.Id:N}/{version.Id:N}/original";
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        var boundary = new SeedBoundary();
        await new CustomerDocumentRegistry(db, boundary, boundary, boundary, new() { Enabled = true }, TimeProvider.System)
            .FinalizeVersionAsync(new(new ClaimsPrincipal()), version,
                [new DocumentAssociation { VersionId = version.Id, CustomerId = customerId, Kind = DocumentResourceKind.Quotation, ResourceId = quotation },
                 new DocumentAssociation { VersionId = version.Id, CustomerId = customerId, Kind = DocumentResourceKind.Order, ResourceId = order }], default);
        return (document.Id, version.Id);
    }

    private sealed class SeedBoundary : ICustomerDocumentAuthority, ICustomerDocumentAssociationValidator, ICustomerDocumentContentEvidence
    {
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) => Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "synthetic-uploader"));
        public Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customerId, IReadOnlyList<DocumentAssociation> links, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
    }
    private sealed class Owners(int customerId, int quotation, int firstOrder) : ICustomerDocumentOwnerReads, INdaOrderConsentReader, INdaEmployeeAuthority
    {
        public bool IncludeLaterOrder { get; set; }
        public DocumentAuthorityOutcome ForwardOutcome { get; set; } = DocumentAuthorityOutcome.Allowed;
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int id, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentCustomer>(DocumentAuthorityOutcome.Allowed, new(customerId)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int id, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentOrder>(DocumentAuthorityOutcome.Allowed, new(id, customerId)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int id, int customer, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentQuotation>(DocumentAuthorityOutcome.Allowed, new(quotation, customerId)));
        public Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int id, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>(ForwardOutcome,
            IncludeLaterOrder ? [new(1, quotation, firstOrder), new(2, quotation, firstOrder + 1)] : [new(1, quotation, firstOrder)]));
        public Task<CustomerDocumentOwnerRead<bool>> ReadAsync(int id, int customer, CancellationToken token) => Task.FromResult(new CustomerDocumentOwnerRead<bool>(DocumentAuthorityOutcome.Allowed, true));
        public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token) => Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "employee"));
    }
}
