using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Resolves canonical NDA coverage from accepted owner reads without invented lineage.</summary>
public sealed class NdaCanonicalCoverageResolver(CustomerDocumentDbContext context,
    ICustomerDocumentOwnerReads? ownerReads = null, IReplacementCaseLineageReader? replacements = null,
    INdaOrderConsentReader? consentReader = null)
{
    /// <summary>Discovers current forward quotation ancestry only for protecting already recorded obligations.</summary>
    public async Task<CanonicalNdaResources> ResolveProtectionAsync(int customerId, IReadOnlyList<NdaCoverageRequest> resources,
        Guid? versionId, CancellationToken token)
    {
        var canonical = await ResolveAsync(customerId, resources, versionId, token);
        if (canonical.Outcome != DocumentAuthorityOutcome.Allowed) return canonical;
        var orders = canonical.Resources.Where(x => x.Kind == DocumentResourceKind.Order).Select(x => x.ResourceId).ToHashSet();
        if (orders.Count == 0) return canonical;
        CanonicalNdaResources Unavailable() => new(DocumentAuthorityOutcome.Unavailable, customerId, []);
        if (await context.Set<NdaRecord>().AnyAsync(x => x.CustomerId == customerId && !x.CoverageSealed, token)) return Unavailable();
        // Calendar status and supersession cannot erase a recorded confidentiality obligation.
        var quotationIds = await (from coverage in context.Set<NdaCoverage>().AsNoTracking()
                                  join agreement in context.Set<NdaRecord>().AsNoTracking() on coverage.NdaId equals agreement.Id
                                  where coverage.CustomerId == customerId && agreement.CustomerId == customerId && agreement.CoverageSealed
                                      && coverage.Kind == DocumentResourceKind.Quotation
                                  select coverage.ResourceId).Distinct().OrderBy(x => x).Take(101).ToArrayAsync(token);
        if (quotationIds.Length > 100) return Unavailable();
        var protectionScope = canonical.Resources.ToHashSet();
        var provenResources = new HashSet<NdaCoverageRequest>();
        foreach (var quotationId in quotationIds)
        {
            var quotation = new NdaCoverageRequest(DocumentResourceKind.Quotation, quotationId);
            var proof = await ResolveAsync(customerId, [quotation], null, token);
            if (proof.Outcome != DocumentAuthorityOutcome.Allowed) return proof;
            NdaAuthorityGuard.Scope(proof, customerId, [quotation], null);
            provenResources.UnionWith(proof.Resources);
            if (provenResources.Count > 2048) return Unavailable();
            if (proof.Resources.Any(x => x.Kind == DocumentResourceKind.Order && orders.Contains(x.ResourceId)))
                protectionScope.Add(quotation);
        }
        if (protectionScope.Count > 2048) return Unavailable();
        // Preserve exact-version binding, visibility and requested-scope consent from the original resolution.
        // Current quote links add a protection ancestor, never evidence of a captured version association.
        return canonical with { Resources = protectionScope.OrderBy(x => x.Kind).ThenBy(x => x.ResourceId).ToArray() };
    }
    /// <summary>Resolves current customer/resource ancestry and optional exact registry version ownership.</summary>
    public async Task<CanonicalNdaResources> ResolveAsync(int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token)
    {
        CanonicalNdaResources Refuse(DocumentAuthorityOutcome outcome) => new(outcome, customerId, []);
        if (ownerReads is null) return Refuse(DocumentAuthorityOutcome.Unavailable);
        ICustomerDocumentOwnerReads owners = ownerReads;
        if (customerId <= 0 || resources.Count is 0 or > 100 || resources.Any(x => x.ResourceId <= 0 || !Enum.IsDefined(x.Kind)))
            return Refuse(DocumentAuthorityOutcome.Denied);
        var customer = await owners.ReadCustomerAsync(customerId, token);
        if (customer.Outcome != DocumentAuthorityOutcome.Allowed) return Refuse(Normalize(customer.Outcome));
        if (customer.Value is null) return Refuse(DocumentAuthorityOutcome.Unavailable);
        if (customer.Value.Id != customerId) return Refuse(DocumentAuthorityOutcome.Denied);
        var requestedScope = new HashSet<NdaCoverageRequest>();
        foreach (var resource in resources)
        {
            var outcome = await Expand(resource, requestedScope, token);
            if (outcome != DocumentAuthorityOutcome.Allowed) return Refuse(outcome);
        }
        var capturedScope = new HashSet<NdaCoverageRequest>();
        DocumentVisibility? visibility = null;
        var resourceVersionBound = false;
        if (versionId is { } id)
        {
            var version = await (from v in context.Versions.AsNoTracking()
                                 join d in context.Documents.AsNoTracking() on v.DocumentId equals d.Id
                                 where v.Id == id
                                 select new { v.CustomerId, DocumentCustomer = d.CustomerId, v.AssociationsSealed, d.Visibility }).SingleOrDefaultAsync(token);
            if (version is null || version.CustomerId != customerId || version.DocumentCustomer != customerId)
                return Refuse(DocumentAuthorityOutcome.Denied);
            if (!version.AssociationsSealed || !Enum.IsDefined(version.Visibility)) return Refuse(DocumentAuthorityOutcome.Unavailable);
            visibility = version.Visibility;
            var captured = await context.Associations.AsNoTracking().Where(x => x.VersionId == id).ToListAsync(token);
            if (captured.Any(x => x.CustomerId != customerId)) return Refuse(DocumentAuthorityOutcome.Denied);
            foreach (var association in captured)
            {
                var outcome = await Expand(new(association.Kind, association.ResourceId), capturedScope, token);
                if (outcome != DocumentAuthorityOutcome.Allowed) return Refuse(outcome);
            }
            resourceVersionBound = resources.All(x => x.Kind == DocumentResourceKind.Customer && x.ResourceId == customerId || capturedScope.Contains(x));
        }
        requestedScope.UnionWith(capturedScope);
        bool? consent = null;
        var orders = requestedScope.Where(x => x.Kind == DocumentResourceKind.Order).Select(x => x.ResourceId).Distinct().Order().ToArray();
        if (orders.Length > 0 && consentReader is not null)
        {
            consent = true;
            foreach (var order in orders)
            {
                var current = await consentReader.ReadAsync(order, customerId, token);
                if (current.Outcome != DocumentAuthorityOutcome.Allowed) { consent = null; break; }
                if (!current.Value) consent = false;
            }
        }
        return new(DocumentAuthorityOutcome.Allowed, customerId, requestedScope.OrderBy(x => x.Kind).ThenBy(x => x.ResourceId).ToArray(),
            versionId, consent, resourceVersionBound, visibility);

        async Task<DocumentAuthorityOutcome> Expand(NdaCoverageRequest resource, HashSet<NdaCoverageRequest> destination, CancellationToken cancellation)
        {
            if (destination.Contains(resource)) return DocumentAuthorityOutcome.Allowed;
            if (resource.ResourceId <= 0 || !Enum.IsDefined(resource.Kind)) return DocumentAuthorityOutcome.Unavailable;
            switch (resource.Kind)
            {
                case DocumentResourceKind.Customer:
                    if (resource.ResourceId != customerId) return DocumentAuthorityOutcome.Denied;
                    break;
                case DocumentResourceKind.Order:
                    var order = await owners.ReadOrderAsync(resource.ResourceId, cancellation);
                    if (order.Outcome != DocumentAuthorityOutcome.Allowed) return Normalize(order.Outcome);
                    if (order.Value is null) return DocumentAuthorityOutcome.Unavailable;
                    if (order.Value.Id != resource.ResourceId || order.Value.CustomerId != customerId) return DocumentAuthorityOutcome.Denied;
                    break;
                case DocumentResourceKind.Quotation:
                    var quotation = await owners.ReadQuotationAsync(resource.ResourceId, customerId, cancellation);
                    if (quotation.Outcome != DocumentAuthorityOutcome.Allowed) return Normalize(quotation.Outcome);
                    if (quotation.Value is null) return DocumentAuthorityOutcome.Unavailable;
                    if (quotation.Value.Id != resource.ResourceId || quotation.Value.CustomerId != customerId) return DocumentAuthorityOutcome.Denied;
                    var links = await owners.ReadQuotationOrdersAsync(resource.ResourceId, cancellation);
                    if (links.Outcome != DocumentAuthorityOutcome.Allowed) return Normalize(links.Outcome);
                    if (links.Value is null || links.Value.Count is 0 or > 512 || links.Value.Any(x => x.Id <= 0 || x.OrderId <= 0 || x.QuotationId != resource.ResourceId)
                        || links.Value.Select(x => x.Id).Distinct().Count() != links.Value.Count || links.Value.Select(x => x.OrderId).Distinct().Count() != links.Value.Count)
                        return DocumentAuthorityOutcome.Unavailable;
                    foreach (var link in links.Value)
                    {
                        var outcome = await Expand(new(DocumentResourceKind.Order, link.OrderId), destination, cancellation);
                        if (outcome != DocumentAuthorityOutcome.Allowed) return outcome;
                    }
                    break;
                case DocumentResourceKind.Replacement:
                    if (replacements is null) return DocumentAuthorityOutcome.Unavailable;
                    var replacement = await replacements.ReadAsync(resource.ResourceId, cancellation);
                    if (replacement.Outcome != DocumentAuthorityOutcome.Allowed) return Normalize(replacement.Outcome);
                    if (replacement.Value is not { } lineage) return DocumentAuthorityOutcome.Unavailable;
                    if (lineage.CaseId != resource.ResourceId || lineage.CustomerId != customerId) return DocumentAuthorityOutcome.Denied;
                    if (lineage.Revision <= 0 || !Ids(lineage.OriginalOrderIds, true) || !Ids(lineage.AttemptIds, false) || !Ids(lineage.ShipmentIds, false))
                        return DocumentAuthorityOutcome.Unavailable;
                    foreach (var original in lineage.OriginalOrderIds)
                    {
                        var outcome = await Expand(new(DocumentResourceKind.Order, original), destination, cancellation);
                        if (outcome != DocumentAuthorityOutcome.Allowed) return outcome;
                    }
                    break;
                default:
                    // Shipment/invoice lineage requires its own accepted owner proof; numeric IDs never substitute.
                    return DocumentAuthorityOutcome.Unavailable;
            }
            destination.Add(resource);
            return DocumentAuthorityOutcome.Allowed;
        }
    }
    private static DocumentAuthorityOutcome Normalize(DocumentAuthorityOutcome outcome) =>
        outcome == DocumentAuthorityOutcome.Denied ? DocumentAuthorityOutcome.Denied : DocumentAuthorityOutcome.Unavailable;
    private static bool Ids(IReadOnlyList<int>? ids, bool nonempty) => ids is not null && ids.Count <= 512 &&
        (!nonempty || ids.Count > 0) && ids.All(x => x > 0) && ids.Distinct().Count() == ids.Count;
}
