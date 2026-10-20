using System.Text.Json;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;

/// <summary>Validates captured associations against canonical current owner reads.</summary>
/// <remarks>Current-session authorization is a separate prerequisite. Customer existence grants no membership or coverage.</remarks>
public sealed class CustomerDocumentAssociationClient : ICustomerDocumentAssociationValidator
{
    private readonly ICustomerDocumentOwnerReads? ownerReads;
    private readonly IReplacementCaseLineageReader? replacementLineage;
    /// <summary>Accepts explicitly installed owner capabilities; missing bindings remain unavailable.</summary>
    public CustomerDocumentAssociationClient(ICustomerDocumentOwnerReads? ownerReads = null, IReplacementCaseLineageReader? replacementLineage = null)
    {
        this.ownerReads = ownerReads;
        this.replacementLineage = replacementLineage;
    }
    /// <summary>Requires exact owner customer identities and every selected quotation/order link.</summary>
    public async Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customerId,
        IReadOnlyList<DocumentAssociation> associations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (customerId <= 0 || associations is null || associations.Count > 128 || associations.Any(item => item is null))
            return DocumentAuthorityOutcome.Denied;
        var selected = associations.Select(item => (item.Kind, item.ResourceId, item.CustomerId)).ToArray();
        if (selected.Any(item => !Enum.IsDefined(item.Kind) || item.ResourceId <= 0 || item.CustomerId != customerId ||
            (item.Kind == DocumentResourceKind.Customer && item.ResourceId != customerId)) ||
            selected.Distinct().Count() != selected.Length || selected.Count(item => item.Kind == DocumentResourceKind.Quotation) > 1)
            return DocumentAuthorityOutcome.Denied;
        if (selected.Any(item => item.Kind is DocumentResourceKind.Invoice or DocumentResourceKind.Shipment) ||
            (selected.Any(item => item.Kind == DocumentResourceKind.Replacement) && replacementLineage is null) || ownerReads is null)
            return DocumentAuthorityOutcome.Unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var customer = await ownerReads.ReadCustomerAsync(customerId, deadline.Token);
            if (customer.Outcome != DocumentAuthorityOutcome.Allowed) return Refusal(customer.Outcome);
            if (customer.Value is null) return DocumentAuthorityOutcome.Unavailable;
            if (customer.Value.Id != customerId) return DocumentAuthorityOutcome.Denied;
            var orderIds = new SortedSet<int>(selected.Where(item => item.Kind == DocumentResourceKind.Order).Select(item => item.ResourceId));
            foreach (var replacement in selected.Where(item => item.Kind == DocumentResourceKind.Replacement))
            {
                var result = await replacementLineage!.ReadAsync(replacement.ResourceId, deadline.Token);
                if (result.Outcome != DocumentAuthorityOutcome.Allowed) return Refusal(result.Outcome);
                var lineage = result.Value;
                if (lineage is null) return DocumentAuthorityOutcome.Unavailable;
                if (lineage.CaseId != replacement.ResourceId || lineage.CustomerId != customerId) return DocumentAuthorityOutcome.Denied;
                if (lineage.Revision <= 0 || !ValidIds(lineage.OriginalOrderIds, true) || !ValidIds(lineage.AttemptIds, false) ||
                    !ValidIds(lineage.ShipmentIds, false)) return DocumentAuthorityOutcome.Unavailable;
                orderIds.UnionWith(lineage.OriginalOrderIds);
                if (orderIds.Count > 512) return DocumentAuthorityOutcome.Unavailable;
            }
            foreach (var orderId in orderIds)
            {
                var order = await ownerReads.ReadOrderAsync(orderId, deadline.Token);
                if (order.Outcome != DocumentAuthorityOutcome.Allowed) return Refusal(order.Outcome);
                if (order.Value is null) return DocumentAuthorityOutcome.Unavailable;
                if (order.Value.Id != orderId || order.Value.CustomerId != customerId) return DocumentAuthorityOutcome.Denied;
            }
            var quotationId = selected.Where(item => item.Kind == DocumentResourceKind.Quotation).Select(item => item.ResourceId).SingleOrDefault();
            if (quotationId != 0)
            {
                var quotation = await ownerReads.ReadQuotationAsync(quotationId, customerId, deadline.Token);
                if (quotation.Outcome != DocumentAuthorityOutcome.Allowed) return Refusal(quotation.Outcome);
                if (quotation.Value is null) return DocumentAuthorityOutcome.Unavailable;
                if (quotation.Value.Id != quotationId || quotation.Value.CustomerId != customerId) return DocumentAuthorityOutcome.Denied;
                if (orderIds.Count != 0)
                {
                    var links = await ownerReads.ReadQuotationOrdersAsync(quotationId, deadline.Token);
                    if (links.Outcome != DocumentAuthorityOutcome.Allowed) return Refusal(links.Outcome);
                    if (links.Value is null) return DocumentAuthorityOutcome.Unavailable;
                    var values = links.Value.ToArray();
                    if (values.Length == 0 || values.Length > 512 || values.Any(link => link is null || link.Id <= 0 ||
                        link.QuotationId != quotationId || link.OrderId <= 0) || values.Select(link => link.Id).Distinct().Count() != values.Length ||
                        values.Select(link => link.OrderId).Distinct().Count() != values.Length ||
                        orderIds.Any(orderId => !values.Any(link => link.OrderId == orderId))) return DocumentAuthorityOutcome.Denied;
                }
            }
            // An explicit order-only set remains order-only; no quotation or acceptance is invented.
            return DocumentAuthorityOutcome.Allowed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or
            ArgumentException or TimeoutException or OperationCanceledException)
        {
            return DocumentAuthorityOutcome.Unavailable;
        }
    }
    private static bool ValidIds(IReadOnlyList<int>? ids, bool nonempty) => ids is not null && ids.Count <= 512 &&
        (!nonempty || ids.Count != 0) && ids.All(id => id > 0) && ids.Distinct().Count() == ids.Count;
    private static DocumentAuthorityOutcome Refusal(DocumentAuthorityOutcome outcome) =>
        outcome == DocumentAuthorityOutcome.Denied ? DocumentAuthorityOutcome.Denied : DocumentAuthorityOutcome.Unavailable;
}
