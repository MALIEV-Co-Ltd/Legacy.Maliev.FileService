using System.Security.Claims;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Carries the authenticated principal to the current-session authority boundary.</summary>
public sealed record DocumentActor(ClaimsPrincipal Principal);
/// <summary>Defines DocumentActorKind values for customer document evidence.</summary>
public enum DocumentActorKind
{
    /// <summary>Employee document evidence state.</summary>
    Employee,
    /// <summary>Member document evidence state.</summary>
    Member,
}
/// <summary>Defines DocumentAuthorityOutcome values for customer document evidence.</summary>
public enum DocumentAuthorityOutcome
{
    /// <summary>Allowed document evidence state.</summary>
    Allowed,
    /// <summary>Denied document evidence state.</summary>
    Denied,
    /// <summary>Unavailable document evidence state.</summary>
    Unavailable,
}
/// <summary>Returns current-session customer authority and the canonical authorized subject.</summary>
public sealed record DocumentAuthorityDecision(DocumentAuthorityOutcome Outcome, DocumentActorKind? ActorKind = null,
    string? AuthorizedSubject = null);
/// <summary>Returns exact-version billing evidence without storage coordinates or URLs.</summary>
public sealed record DocumentEvidenceReceipt(Guid DocumentId, Guid VersionId, int CustomerId, DocumentKind Kind,
    string ContentSha256, int? QuotationId, int[] OrderIds, VerificationStatus VerificationStatus,
    string? VerifiedBySubject, DateTimeOffset? VerifiedAtUtc, long Revision);
/// <summary>Describes authorized document metadata for bounded summary consumers.</summary>
public sealed record DocumentSummary(Guid DocumentId, int CustomerId, DocumentKind Kind, string Title,
    DocumentVisibility Visibility, long Revision);
/// <summary>Describes authorized sealed version history without private storage coordinates.</summary>
public sealed record DocumentVersionSummary(Guid DocumentId, Guid VersionId, int VersionNumber, DocumentKind Kind,
    string ContentSha256, DateTimeOffset CreatedAtUtc, VerificationStatus VerificationStatus,
    string? VerifiedBySubject, DateTimeOffset? VerifiedAtUtc, long Revision);
/// <summary>Defines granular customer document permissions without granting them to identities.</summary>
public static class CustomerDocumentPermissions
{
    /// <summary>Requires customer-scoped Read authority.</summary>
    public const string Read = "legacy-file.documents.read";
    /// <summary>Requires customer-scoped Write authority.</summary>
    public const string Write = "legacy-file.documents.write";
    /// <summary>Requires customer-scoped Verify authority.</summary>
    public const string Verify = "legacy-file.documents.verify";
    /// <summary>Requires customer-scoped Archive authority.</summary>
    public const string Archive = "legacy-file.documents.archive";
    /// <summary>Requires customer-scoped EvaluateProtection authority.</summary>
    public const string EvaluateProtection = "legacy-file.protection.evaluate";
}
/// <summary>Indicates that required current authoritative evidence cannot be established.</summary>
public sealed class DocumentAuthorityUnavailableException : Exception;
/// <summary>Indicates that the current actor or association is outside authorized customer scope.</summary>
public sealed class DocumentAuthorityDeniedException : Exception;
