namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

/// <summary>Returns a canonical owner read, never a local association assertion.</summary>
public sealed record CustomerDocumentOwnerRead<T>(DocumentAuthorityOutcome Outcome, T? Value = default);

/// <summary>Canonical customer existence only; does not establish membership or legal coverage.</summary>
public sealed record CanonicalDocumentCustomer(int Id);

/// <summary>Order owner customer evidence; no quotation identity is supplied by this owner route.</summary>
public sealed record CanonicalDocumentOrder(int Id, int CustomerId);

/// <summary>Customer-scoped quotation evidence; acceptance is deliberately not inferred.</summary>
public sealed record CanonicalDocumentQuotation(int Id, int CustomerId);

/// <summary>Exact owner-returned quotation and order link.</summary>
public sealed record CanonicalDocumentOrderLink(int Id, int QuotationId, int OrderId);

/// <summary>CRM-owned canonical existence boundary, pending an accepted CRM transport contract.</summary>
public interface ICanonicalDocumentCustomerReader
{
    /// <summary>Reads the exact customer through the accepted CRM owner contract.</summary>
    Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadAsync(int customerId, CancellationToken token);
}

/// <summary>Owner service reads required for canonical associations, independent of current-session authorization.</summary>
public interface ICustomerDocumentOwnerReads
{
    /// <summary>Reads canonical customer existence.</summary>
    Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int customerId, CancellationToken token);
    /// <summary>Reads canonical order customer ownership.</summary>
    Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int orderId, CancellationToken token);
    /// <summary>Reads the exact customer-scoped quotation.</summary>
    Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int quotationId, int customerId, CancellationToken token);
    /// <summary>Reads current live-authorized quotation links.</summary>
    Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int quotationId, CancellationToken token);
}

/// <summary>Server credential boundary; implementations use the approved service token provider, never browser tokens.</summary>
public interface ICustomerDocumentOwnerCredential
{
    /// <summary>Gets a short-lived server credential for this configured owner origin or returns unavailable.</summary>
    Task<string?> GetAccessTokenAsync(Uri ownerOrigin, CancellationToken token);
}
