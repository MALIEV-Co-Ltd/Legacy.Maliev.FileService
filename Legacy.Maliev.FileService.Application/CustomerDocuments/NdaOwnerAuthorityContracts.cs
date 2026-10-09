namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Requires the accepted Auth owner contract for active employee identity and staff worklists.</summary>
public interface INdaEmployeeAuthority
{
    /// <summary>Confirms a current active employee, never inferred from JWT identity/name.</summary>
    Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token);
    /// <summary>Checks current employee session and own-worklist scoped permission.</summary>
    Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token);
}
/// <summary>Requires current consent readback from the order owner, independently of client input.</summary>
public interface INdaOrderConsentReader
{
    /// <summary>Returns owner-confirmed current social consent for one exact customer/order.</summary>
    Task<CustomerDocumentOwnerRead<bool>> ReadAsync(int orderId, int customerId, CancellationToken token);
}
