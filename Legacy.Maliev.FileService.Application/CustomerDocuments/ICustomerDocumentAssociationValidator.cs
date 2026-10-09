using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Requires canonical resource ownership confirmation from each resource owner.</summary>
public interface ICustomerDocumentAssociationValidator
{
    /// <summary>Checks current authoritative customer document evidence.</summary>
    Task<DocumentAuthorityOutcome> ValidateAsync(DocumentActor actor, int customerId,
        IReadOnlyList<DocumentAssociation> associations, CancellationToken token);
}
