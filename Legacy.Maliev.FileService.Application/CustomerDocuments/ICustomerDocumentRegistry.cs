namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

using Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Defines authorized exact-version evidence readback.</summary>
public interface ICustomerDocumentRegistry
{
    /// <summary>Checks current authoritative customer document evidence.</summary>
    Task<DocumentEvidenceReceipt?> ReadReceiptAsync(DocumentActor actor, int customerId, Guid documentId,
        Guid versionId, CancellationToken token);
    /// <summary>Authorizes and atomically seals a clean exact version and its canonical association set.</summary>
    Task FinalizeVersionAsync(DocumentActor actor, CustomerDocumentVersion version,
        IReadOnlyList<DocumentAssociation> associations, CancellationToken token);
    /// <summary>Reads a bounded page of documents with authorized sealed clean versions.</summary>
    Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentActor actor, int customerId, int offset, int limit, CancellationToken token);
    /// <summary>Reads a bounded page of authorized sealed clean version history.</summary>
    Task<IReadOnlyList<DocumentVersionSummary>?> ReadVersionsAsync(DocumentActor actor, int customerId,
        Guid documentId, int offset, int limit, CancellationToken token);
}
