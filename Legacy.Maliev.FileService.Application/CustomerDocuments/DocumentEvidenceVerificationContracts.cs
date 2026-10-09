namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

/// <summary>Requests an append-only decision for an exact non-NDA verification revision.</summary>
public sealed record DocumentEvidenceVerificationRequest(long ExpectedVerificationRevision, string Status, string Reason);

/// <summary>Appends staff evidence after current authority, clean bytes and canonical links are proved.</summary>
public interface IDocumentEvidenceVerificationService
{
    /// <summary>Returns the exact receipt after an atomic verification event and audit append.</summary>
    Task<DocumentEvidenceReceipt?> VerifyAsync(DocumentActor actor, int customerId, Guid documentId, Guid versionId,
        DocumentEvidenceVerificationRequest request, CancellationToken token);
}

/// <summary>Reports that the selected exact verification revision has advanced.</summary>
public sealed class DocumentVerificationConflictException : Exception;

/// <summary>Reports that NDA evidence requires its legal lifecycle endpoint.</summary>
public sealed class DocumentVerificationKindException : Exception;

/// <summary>Requires independently proved current active employment for a canonical staff subject.</summary>
public interface IDocumentVerificationEmployeeAuthority
{
    /// <summary>Checks active employment against its authoritative owner.</summary>
    Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token);
}
