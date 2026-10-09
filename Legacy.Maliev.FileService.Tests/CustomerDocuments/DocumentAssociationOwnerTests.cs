using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class DocumentAssociationOwnerTests
{
    private static readonly DocumentActor Actor = new(new ClaimsPrincipal());
    [Fact]
    public async Task Order_only_owner_evidence_allows_without_fictional_quotation()
    {
        var client = new CustomerDocumentAssociationClient(new ControlledOwnerReads());
        Assert.Equal(DocumentAuthorityOutcome.Allowed, await client.ValidateAsync(Actor, 7,
            [Association(DocumentResourceKind.Order, 31)], CancellationToken.None));
    }
    [Fact]
    public async Task Quotation_requires_exact_owner_link_for_each_selected_order()
    {
        var reads = new ControlledOwnerReads { Links = [new(1, 9, 32)] };
        var client = new CustomerDocumentAssociationClient(reads);
        Assert.Equal(DocumentAuthorityOutcome.Denied, await client.ValidateAsync(Actor, 7,
            [Association(DocumentResourceKind.Quotation, 9), Association(DocumentResourceKind.Order, 31)], CancellationToken.None));
    }
    [Fact]
    public async Task Missing_integration_is_unavailable()
    {
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, await new CustomerDocumentAssociationClient().ValidateAsync(
            Actor, 7, [Association(DocumentResourceKind.Customer, 7)], CancellationToken.None));
    }
    [Theory]
    [InlineData(DocumentResourceKind.Customer, 8, DocumentAuthorityOutcome.Denied)]
    [InlineData(DocumentResourceKind.Invoice, 44, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(DocumentResourceKind.Replacement, 44, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(DocumentResourceKind.Shipment, 44, DocumentAuthorityOutcome.Unavailable)]
    public async Task Customer_identity_and_missing_other_owner_contracts_fail_closed(DocumentResourceKind kind, int id, DocumentAuthorityOutcome expected)
    {
        var client = new CustomerDocumentAssociationClient(new ControlledOwnerReads());
        Assert.Equal(expected, await client.ValidateAsync(Actor, 7, [Association(kind, id)], CancellationToken.None));
    }
    [Fact]
    public async Task Multiple_quotations_duplicate_resources_and_cross_customer_sets_refuse()
    {
        var client = new CustomerDocumentAssociationClient(new ControlledOwnerReads());
        Assert.Equal(DocumentAuthorityOutcome.Denied, await client.ValidateAsync(Actor, 7,
            [Association(DocumentResourceKind.Quotation, 9), Association(DocumentResourceKind.Quotation, 10)], CancellationToken.None));
        Assert.Equal(DocumentAuthorityOutcome.Denied, await client.ValidateAsync(Actor, 7,
            [Association(DocumentResourceKind.Order, 31), Association(DocumentResourceKind.Order, 31)], CancellationToken.None));
        var wrongTenant = Association(DocumentResourceKind.Order, 31);
        wrongTenant.CustomerId = 8;
        Assert.Equal(DocumentAuthorityOutcome.Denied, await client.ValidateAsync(Actor, 7, [wrongTenant], CancellationToken.None));
    }
    [Fact]
    public async Task Optional_replacement_lineage_requires_all_original_order_customers()
    {
        var lineage = new ControlledReplacementLineage();
        var client = new CustomerDocumentAssociationClient(new ControlledOwnerReads(), lineage);
        Assert.Equal(DocumentAuthorityOutcome.Allowed, await client.ValidateAsync(Actor, 7,
            [Association(DocumentResourceKind.Replacement, 44)], CancellationToken.None));
        client = new CustomerDocumentAssociationClient(new ControlledOwnerReads { WrongCustomerOrderId = 32 }, lineage);
        Assert.Equal(DocumentAuthorityOutcome.Denied, await client.ValidateAsync(Actor, 7,
            [Association(DocumentResourceKind.Replacement, 44)], CancellationToken.None));
    }
    // Pending-owner capability control only; no replacement HTTP contract is installed.
    private sealed class ControlledReplacementLineage : IReplacementCaseLineageReader
    {
        public Task<CustomerDocumentOwnerRead<ReplacementCaseLineage>> ReadAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<ReplacementCaseLineage>(DocumentAuthorityOutcome.Allowed,
                new(id, 7, [31, 32], [1], [2], 1)));
    }
    private static DocumentAssociation Association(DocumentResourceKind kind, int id) => new() { CustomerId = 7, Kind = kind, ResourceId = id };
    // Synthetic controls. These IDs are not canonical owner receipts.
    private sealed class ControlledOwnerReads : ICustomerDocumentOwnerReads
    {
        public int? WrongCustomerOrderId { get; init; }
        public IReadOnlyList<CanonicalDocumentOrderLink> Links { get; init; } = [new(1, 9, 31)];
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentCustomer>(DocumentAuthorityOutcome.Allowed, new(id)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentOrder>(DocumentAuthorityOutcome.Allowed, new(id, id == WrongCustomerOrderId ? 8 : 7)));
        public Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int id, int customerId, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<CanonicalDocumentQuotation>(DocumentAuthorityOutcome.Allowed, new(id, customerId)));
        public Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int id, CancellationToken token) =>
            Task.FromResult(new CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>(DocumentAuthorityOutcome.Allowed, Links));
    }
}
