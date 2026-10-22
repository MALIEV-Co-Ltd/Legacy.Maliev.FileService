namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Requires current clean scan and immutable generation evidence for an exact version.</summary>
public interface ICustomerDocumentContentEvidence
{
    /// <summary>Confirms clean bytes, digest and exact generation; absence or uncertainty is unavailable.</summary>
    Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token);
}
