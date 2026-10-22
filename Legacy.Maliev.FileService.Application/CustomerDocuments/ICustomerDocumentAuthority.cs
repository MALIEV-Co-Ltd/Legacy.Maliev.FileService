namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
// Implementations MUST check current session, trusted member tenant and customer-scoped permission.
// Token identity/name/company/email alone never grants authority.
/// <summary>Requires current-session identity, canonical customer scope and resource permission.</summary>
public interface ICustomerDocumentAuthority
{
    /// <summary>Checks current authoritative customer document evidence.</summary>
    Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token);
}
