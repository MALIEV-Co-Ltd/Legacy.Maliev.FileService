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
public sealed class NdaInternalVisibilityPostgreSqlHttpTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Theory]
    [InlineData(ProtectionAction.Read, true)]
    [InlineData(ProtectionAction.ExternalShare, false)]
    [InlineData(ProtectionAction.PublishSocial, false)]
    [InlineData(ProtectionAction.PublicExport, false)]
    public async Task SealedInternalVersionAllowsScopedEmployeeReadAndDeniesAllDisclosureWithoutNda(ProtectionAction action, bool allowed)
    {
        // A distinct customer prevents another provider test's NDA from hiding the classification defect.
        var customerId = Random.Shared.Next(100000, 2000000000);
        var orderId = Random.Shared.Next(100000, 2000000000);
        var versionId = await SeedInternal(customerId, orderId);
        await using var factory = new NdaHttpFactory(services =>
        {
            services.AddCustomerDocuments(fixture.Connection, enabled: true);
            services.AddCustomerDocumentNda(new NdaOptions { Enabled = true });
            services.AddScoped<ICustomerDocumentOwnerReads>(_ => new Owners(customerId, orderId));
            services.AddScoped<INdaOrderConsentReader>(_ => new Owners(customerId, orderId));
            services.AddScoped<INdaAuthority>(provider => new NdaOwnerAuthority(new SyntheticAuthority(DocumentActorKind.Employee),
                provider.GetRequiredService<NdaCanonicalCoverageResolver>()));
        });
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.EvaluateProtection);
        using var response = await client.PostAsJsonAsync($"customers/{customerId}/protection/evaluate",
            new ProtectionRequest(DocumentResourceKind.Order, orderId, action, true, versionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = await response.Content.ReadFromJsonAsync<ProtectionDecision>();
        Assert.NotNull(decision);
        Assert.Equal(allowed, decision.Allowed);
        Assert.False(decision.Protected); // This is classification protection, with no NDA obligation to mask the rule.
        await using var read = fixture.CreateContext();
        Assert.Empty(await read.Set<NdaRecord>().Where(x => x.CustomerId == customerId).ToListAsync());
        Assert.True((await read.Versions.SingleAsync(x => x.Id == versionId)).AssociationsSealed);
    }

    private async Task<Guid> SeedInternal(int customerId, int orderId)
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        var document = new CustomerDocument
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Kind = DocumentKind.Nda,
            Title = "Synthetic internal record",
            Visibility = DocumentVisibility.Internal
        };
        var version = new CustomerDocumentVersion
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            CustomerId = customerId,
            Kind = document.Kind,
            VersionNumber = 1,
            ContentSha256 = new string('a', 64),
            ActorSubject = "synthetic-uploader",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StorageBucket = "synthetic-private",
            StorageGeneration = 1,
            ContentSize = 5,
            ContentType = "application/pdf",
            OriginalFileName = "synthetic.pdf",
            ScanOperationId = Guid.NewGuid(),
            ScanSourceGeneration = 1
        };
        version.StorageObjectName = $"customer-documents/{customerId}/{document.Id:N}/{version.Id:N}/original";
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        var boundary = new Boundary();
        await new CustomerDocumentRegistry(db, boundary, boundary, boundary, new() { Enabled = true }, TimeProvider.System)
            .FinalizeVersionAsync(new(new ClaimsPrincipal()), version,
                [new DocumentAssociation { VersionId = version.Id, CustomerId = customerId, Kind = DocumentResourceKind.Order, ResourceId = orderId }], default);
        return version.Id;
    }

    private sealed class Boundary : ICustomerDocumentAuthority, ICustomerDocumentAssociationValidator, ICustomerDocumentContentEvidence
    {
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) =>
            Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "synthetic-uploader"));
        public Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customerId, IReadOnlyList<DocumentAssociation> associations, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
    }
    private sealed class Owners(int customerId, int orderId) : ICustomerDocumentOwnerReads, INdaOrderConsentReader
    {
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentCustomer>(DocumentAuthorityOutcome.Allowed, new(customerId)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentOrder>(DocumentAuthorityOutcome.Allowed, new(orderId, customerId)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int id, int customer, CancellationToken token) => throw new NotSupportedException();
        public Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int id, CancellationToken token) => throw new NotSupportedException();
        public Task<CustomerDocumentOwnerRead<bool>> ReadAsync(int id, int customer, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<bool>(DocumentAuthorityOutcome.Allowed, true));
    }
}
