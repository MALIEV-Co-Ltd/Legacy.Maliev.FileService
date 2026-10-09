using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class NdaCanonicalCoverageTests
{
    [Fact]
    public async Task MissingReplacementOwnerCannotClaimUnprotectedCoverage()
    {
        await using var db = Context();
        var resolver = new NdaCanonicalCoverageResolver(db, new SyntheticOwnerReads());
        var result = await resolver.ResolveAsync(23, [new(DocumentResourceKind.Replacement, 17)], null, default);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, result.Outcome);
    }
    [Fact]
    public async Task ReplacementCoverageIncludesAllCanonicalOriginalOrders()
    {
        await using var db = Context();
        var resolver = new NdaCanonicalCoverageResolver(db, new SyntheticOwnerReads(), new SyntheticReplacementLineage());
        var result = await resolver.ResolveAsync(23, [new(DocumentResourceKind.Replacement, 17)], null, default);
        Assert.Equal(DocumentAuthorityOutcome.Allowed, result.Outcome);
        Assert.Contains(new NdaCoverageRequest(DocumentResourceKind.Order, 81), result.Resources);
        Assert.Contains(new NdaCoverageRequest(DocumentResourceKind.Order, 82), result.Resources);
        Assert.DoesNotContain(new NdaCoverageRequest(DocumentResourceKind.Shipment, 999), result.Resources);
    }
    [Fact]
    public async Task OneForeignOriginalOrderDeniesEntireReplacementScope()
    {
        await using var db = Context();
        var resolver = new NdaCanonicalCoverageResolver(db, new SyntheticOwnerReads { ForeignOrder = 82 }, new SyntheticReplacementLineage());
        Assert.Equal(DocumentAuthorityOutcome.Denied, (await resolver.ResolveAsync(23, [new(DocumentResourceKind.Replacement, 17)], null, default)).Outcome);
    }
    [Fact]
    public async Task MissingShipmentOwnerProofIsUnavailable()
    {
        await using var db = Context();
        var resolver = new NdaCanonicalCoverageResolver(db, new SyntheticOwnerReads(), new SyntheticReplacementLineage());
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, (await resolver.ResolveAsync(23, [new(DocumentResourceKind.Shipment, 999)], null, default)).Outcome);
    }
    private static CustomerDocumentDbContext Context() => new(new DbContextOptionsBuilder<CustomerDocumentDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=synthetic;Username=synthetic").Options);
}
internal sealed class SyntheticReplacementLineage : IReplacementCaseLineageReader
{
    public Task<CustomerDocumentOwnerRead<ReplacementCaseLineage>> ReadAsync(int caseId, CancellationToken token) =>
        Task.FromResult(new CustomerDocumentOwnerRead<ReplacementCaseLineage>(DocumentAuthorityOutcome.Allowed,
            new(caseId, 23, [81, 82], [777], [999], 4)));
}
internal sealed class SyntheticOwnerReads : ICustomerDocumentOwnerReads
{
    public int? ForeignOrder { get; set; }
    public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int customerId, CancellationToken token) =>
        Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentCustomer>(DocumentAuthorityOutcome.Allowed, new(customerId)));
    public Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int orderId, CancellationToken token) =>
        Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentOrder>(DocumentAuthorityOutcome.Allowed, new(orderId, orderId == ForeignOrder ? 24 : 23)));
    public Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int quotationId, int customerId, CancellationToken token) =>
        Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentQuotation>(DocumentAuthorityOutcome.Allowed, new(quotationId, customerId)));
    public Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int quotationId, CancellationToken token) =>
        Task.FromResult(new CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>(DocumentAuthorityOutcome.Allowed, [new(1, quotationId, 81)]));
}
