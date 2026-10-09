using System.Text.Json.Serialization;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Specifies explicit canonical coverage; no company or email inference is permitted.</summary>
public sealed record NdaCoverageRequest([property: JsonConverter(typeof(JsonStringEnumConverter<DocumentResourceKind>))] DocumentResourceKind Kind, int ResourceId);
/// <summary>Requests staff review of an exact externally executed signed version.</summary>
public sealed record NdaVerificationRequest(Guid VersionId, long ExpectedRevision, string PartyOne, string PartyTwo,
    DateTimeOffset EffectiveAtUtc, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? RenewalAtUtc,
    NdaSurvivalKind SurvivalKind, DateTimeOffset? SurvivalEndsAtUtc, string ResponsibleEmployeeSubject,
    IReadOnlyList<NdaCoverageRequest> Coverage, string Reason);
/// <summary>Returns exact immutable verification identity and independent lifecycle states.</summary>
public sealed record NdaVerificationReceipt(Guid NdaId, Guid DocumentId, Guid VersionId, long VerificationRevision,
    AgreementCalendarStatus AgreementStatus, ConfidentialityObligationStatus ObligationStatus);
/// <summary>Returns employee-only calendar/survival indicators computed from immutable agreement evidence.</summary>
public sealed record NdaAgreementSummary(Guid NdaId, Guid DocumentId, Guid VersionId, long VerificationRevision,
    DateTimeOffset EffectiveAtUtc, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? RenewalAtUtc,
    NdaSurvivalKind SurvivalKind, DateTimeOffset? SurvivalEndsAtUtc, string ResponsibleEmployeeSubject,
    AgreementCalendarStatus AgreementStatus, ConfidentialityObligationStatus ObligationStatus);
/// <summary>Defines scoped work and disclosure actions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProtectionAction>))]
public enum ProtectionAction
{
    /// <summary>Allows legitimate authorized confidential work.</summary>
    Read,
    /// <summary>Requests disclosure outside scoped authorized work.</summary>
    ExternalShare,
    /// <summary>Requests publication to social media.</summary>
    PublishSocial,
    /// <summary>Requests public export of covered work.</summary>
    PublicExport,
}
/// <summary>Requests authoritative protection evaluation without an override field.</summary>
public sealed record ProtectionRequest([property: JsonConverter(typeof(JsonStringEnumConverter<DocumentResourceKind>))] DocumentResourceKind Kind, int ResourceId, ProtectionAction Action,
    bool SocialConsent = false, Guid? VersionId = null);
/// <summary>Returns a policy decision; unavailable authority throws rather than implying permission.</summary>
public sealed record ProtectionDecision(bool Allowed, bool Protected, ConfidentialityObligationStatus ObligationStatus, string Reason);
/// <summary>Returns internal employee work without agreement bodies or customer recipients.</summary>
public sealed record InternalNdaReminderSummary(Guid Id, Guid NdaId, Guid VersionId, long RenewalRevision, int LeadDays,
    string ResponsibleEmployeeSubject, DateTimeOffset DueAtUtc, InternalNdaReminderState State);
/// <summary>Returns canonical owner readback including inherited original-order coverage.</summary>
public sealed record CanonicalNdaResources(DocumentAuthorityOutcome Outcome, int CustomerId,
    IReadOnlyList<NdaCoverageRequest> Resources, Guid? ConfirmedVersionId = null, bool? ConfirmedSocialConsent = null,
    bool ConfirmedResourceVersionAssociation = false, DocumentVisibility? ConfirmedVersionVisibility = null);
/// <summary>Returns exact version evidence required before staff verification.</summary>
public sealed record NdaVerificationTarget(Guid DocumentId, Guid VersionId, int CustomerId, DocumentKind Kind, string ContentSha256, long Revision);
/// <summary>Returns one immutable agreement and whether a later record superseded its calendar.</summary>
public sealed record NdaAgreementReadback(NdaRecord Record, bool Superseded);
/// <summary>Defaults runtime and daily reminder scheduling to disabled.</summary>
public sealed class NdaOptions
{
    /// <summary>Gets or sets explicit module activation, disabled by default.</summary>
    public bool Enabled { get; set; }
    /// <summary>Gets or sets independent scheduler activation, disabled by default.</summary>
    public bool SchedulerEnabled { get; set; }
    /// <summary>Gets or sets the explicit reminder calendar timezone.</summary>
    public string TimeZoneId { get; set; } = "Asia/Bangkok";
    /// <summary>Gets or sets bounded configurable calendar lead days.</summary>
    public int[] LeadDays { get; set; } = [30, 14, 7, 1, 0];
}
/// <summary>Indicates invalid externally signed agreement metadata.</summary>
public sealed class NdaValidationException : Exception;
/// <summary>Indicates a stale immutable verification revision.</summary>
public sealed class NdaRevisionConflictException : Exception;
/// <summary>Indicates no scoped exact NDA agreement version was found.</summary>
public sealed class NdaNotFoundException : Exception;
/// <summary>Requires current customer, employee, canonical lineage and staff-worklist owner authority.</summary>
public interface INdaAuthority : ICustomerDocumentAuthority
{
    /// <summary>Checks the employee remains active at the current authority owner.</summary>
    Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token);
    /// <summary>Resolves requested resources and owner-confirmed ancestors within one customer.</summary>
    Task<CanonicalNdaResources> ResolveCoverageAsync(DocumentActor actor, int customerId,
        IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token);
    /// <summary>Resolves protection-only historical scope ancestry without certifying new legal coverage.</summary>
    Task<CanonicalNdaResources> ResolveProtectionCoverageAsync(DocumentActor actor, int customerId,
        IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token);
    /// <summary>Authorizes only current employee sessions for their own responsible worklist.</summary>
    Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token);
}
/// <summary>Persists atomic append-only agreements and deduplicated internal reminder tasks.</summary>
public interface INdaRepository
{
    /// <summary>Reads the latest record or latest verification of one exact signed version within customer scope.</summary>
    Task<NdaAgreementReadback?> ReadAgreementAsync(int customerId, Guid documentId, Guid? versionId, CancellationToken token);
    /// <summary>Reads one exact scoped agreement version and current document revision.</summary>
    Task<NdaVerificationTarget?> ReadVerificationTargetAsync(int customerId, Guid documentId, Guid versionId, CancellationToken token);
    /// <summary>Atomically compares revision, appends evidence and cancels stale future work.</summary>
    Task<NdaRecord> AppendVerificationAsync(NdaRecord record, IReadOnlyList<NdaCoverageRequest> coverage, long expectedRevision, string reason, CancellationToken token);
    /// <summary>Reads every covered record, including expired and superseded agreements.</summary>
    Task<IReadOnlyList<NdaRecord>> ReadCoveredAgreementsAsync(int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token);
    /// <summary>Reads latest immutable records for scheduler evaluation.</summary>
    Task<IReadOnlyList<NdaRecord>> ReadCurrentAgreementsAsync(CancellationToken token);
    /// <summary>Rechecks the renewal epoch and atomically queues a unique durable task.</summary>
    Task<int> QueueReminderAsync(NdaRecord record, int leadDays, DateTimeOffset dueAtUtc, DateTimeOffset now, CancellationToken token);
    /// <summary>Reads a bounded worklist restricted to one current responsible employee.</summary>
    Task<IReadOnlyList<InternalNdaReminderSummary>> ReadWorklistAsync(string responsibleSubject, int limit, CancellationToken token,
        DateTimeOffset? dueFromUtc = null, DateTimeOffset? dueThroughUtc = null, InternalNdaReminderState? state = null);
}
/// <summary>Provides a mandatory server-side protection boundary for covered output producers.</summary>
public interface IDocumentProtectionService
{
    /// <summary>Evaluates current authority and surviving confidentiality before an action.</summary>
    Task<ProtectionDecision> EvaluateAsync(DocumentActor actor, int customerId, ProtectionRequest request, CancellationToken token);
}
/// <summary>Provides scoped internal reminder evaluation for the explicitly enabled daily worker.</summary>
public interface INdaReminderQueue
{
    /// <summary>Evaluates durable responsible-staff tasks without an external send transport.</summary>
    Task<int> QueueDueAsync(CancellationToken token);
}
/// <summary>Requires complete authority evidence and fails closed for missing enum/subject values.</summary>
public static class NdaAuthorityGuard
{
    /// <summary>Requires a successful owner response without accepting unknown states.</summary>
    public static void Require(DocumentAuthorityOutcome outcome)
    {
        if (outcome == DocumentAuthorityOutcome.Denied) throw new DocumentAuthorityDeniedException();
        if (outcome != DocumentAuthorityOutcome.Allowed) throw new DocumentAuthorityUnavailableException();
    }
    /// <summary>Returns a complete canonical identity, optionally requiring an employee.</summary>
    public static string Subject(DocumentAuthorityDecision decision, bool employeeOnly = false)
    {
        Require(decision.Outcome);
        if (decision.ActorKind is null || !Enum.IsDefined(decision.ActorKind.Value) || string.IsNullOrWhiteSpace(decision.AuthorizedSubject))
            throw new DocumentAuthorityUnavailableException();
        if (employeeOnly && decision.ActorKind != DocumentActorKind.Employee) throw new DocumentAuthorityDeniedException();
        return decision.AuthorizedSubject;
    }
    /// <summary>Checks that current canonical readback includes the exact requested scope.</summary>
    public static void Scope(CanonicalNdaResources canonical, int customerId, IReadOnlyList<NdaCoverageRequest> requested, Guid? versionId)
    {
        Require(canonical.Outcome);
        if (canonical.CustomerId != customerId) throw new DocumentAuthorityDeniedException();
        if (canonical.Resources is null || canonical.Resources.Count is 0 or > 2048 || canonical.Resources.Distinct().Count() != canonical.Resources.Count || canonical.Resources.Any(x => x.ResourceId <= 0 || !Enum.IsDefined(x.Kind) || x.Kind == DocumentResourceKind.Customer && x.ResourceId != customerId)
            || requested.Any(x => !canonical.Resources.Contains(x)) || versionId is not null && canonical.ConfirmedVersionId != versionId)
            throw new DocumentAuthorityUnavailableException();
    }
}
