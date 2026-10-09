namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

/// <summary>Owner lineage for one replacement case; identities are never inferred from local documents.</summary>
public sealed record ReplacementCaseLineage(int CaseId, int CustomerId,
    IReadOnlyList<int> OriginalOrderIds, IReadOnlyList<int> AttemptIds, IReadOnlyList<int> ShipmentIds, long Revision);

/// <summary>Optional replacement owner capability pending accepted source, authorization and runtime binding.</summary>
/// <remarks>The proposed GET lineage shape is not an accepted transport contract. No endpoint implementation or default
/// successful response is supplied. Consumers must refuse when this capability is absent or unavailable.</remarks>
public interface IReplacementCaseLineageReader
{
    /// <summary>Reads current canonical case lineage through the owner-approved contract.</summary>
    Task<CustomerDocumentOwnerRead<ReplacementCaseLineage>> ReadAsync(int caseId, CancellationToken token);
}
