using System.Text.Json.Serialization;
namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;

/// <summary>Defines explicit survival evidence from the externally signed agreement.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NdaSurvivalKind>))]
public enum NdaSurvivalKind
{
    /// <summary>Survival needs staff review; confidentiality remains protected.</summary>
    Unknown,
    /// <summary>Confidentiality survives indefinitely.</summary>
    Indefinite,
    /// <summary>A calendar date exists but never grants release automatically.</summary>
    Finite,
}
/// <summary>Describes only the agreement calendar, independently of confidentiality.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgreementCalendarStatus>))]
public enum AgreementCalendarStatus
{
    /// <summary>The agreement effective date is in the future.</summary>
    Future,
    /// <summary>The agreement is within its effective calendar.</summary>
    Active,
    /// <summary>The agreement calendar has expired.</summary>
    Expired,
    /// <summary>A later immutable agreement record supersedes its lifecycle.</summary>
    Superseded,
}
/// <summary>Describes confidentiality; V1 never releases a covered agreement.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfidentialityObligationStatus>))]
public enum ConfidentialityObligationStatus
{
    /// <summary>The confidentiality obligation protects all covered material.</summary>
    Protected,
    /// <summary>Staff review is required while confidentiality stays protected.</summary>
    ReviewRequired,
    /// <summary>Reserved for a separately authorized future release workflow.</summary>
    Released,
}
/// <summary>Retains immutable verification of one externally executed agreement version.</summary>
public sealed class NdaRecord
{
    /// <summary>Gets or sets the immutable record identity.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the agreement document identity.</summary>
    public Guid DocumentId { get; set; }
    /// <summary>Gets or sets the exact clean signed version.</summary>
    public Guid VersionId { get; set; }
    /// <summary>Gets or sets canonical customer ownership.</summary>
    public int CustomerId { get; set; }
    /// <summary>Gets or sets the first explicitly reviewed party.</summary>
    public string PartyOne { get; set; } = "";
    /// <summary>Gets or sets the second explicitly reviewed party.</summary>
    public string PartyTwo { get; set; } = "";
    /// <summary>Gets or sets the UTC effective instant.</summary>
    public DateTimeOffset EffectiveAtUtc { get; set; }
    /// <summary>Gets or sets optional UTC agreement expiry.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    /// <summary>Gets or sets an optional UTC renewal reminder calendar.</summary>
    public DateTimeOffset? RenewalAtUtc { get; set; }
    /// <summary>Gets or sets explicit survival classification.</summary>
    public NdaSurvivalKind SurvivalKind { get; set; }
    /// <summary>Gets or sets a finite survival date that requires review rather than release.</summary>
    public DateTimeOffset? SurvivalEndsAtUtc { get; set; }
    /// <summary>Gets or sets the current active employee verified as responsible.</summary>
    public string ResponsibleEmployeeSubject { get; set; } = "";
    /// <summary>Gets or sets the active employee who verified this exact agreement.</summary>
    public string VerifiedBySubject { get; set; } = "";
    /// <summary>Gets or sets the immutable verification UTC instant.</summary>
    public DateTimeOffset VerifiedAtUtc { get; set; }
    /// <summary>Gets or sets the immutable evidence revision and reminder epoch.</summary>
    public long VerificationRevision { get; set; }
    /// <summary>Gets or sets the prior record without rewriting its surviving obligations.</summary>
    public Guid? SupersedesNdaId { get; set; }
    /// <summary>Gets or sets the irreversible atomic seal for the reviewed legal coverage set.</summary>
    public bool CoverageSealed { get; set; }
}
/// <summary>Computes independent calendar and obligation states without a release shortcut.</summary>
public static class NdaLifecycle
{
    /// <summary>Returns agreement calendar state; supersession never affects protection.</summary>
    public static AgreementCalendarStatus CalendarStatus(NdaRecord record, DateTimeOffset now, bool superseded) =>
        superseded ? AgreementCalendarStatus.Superseded : now < record.EffectiveAtUtc ? AgreementCalendarStatus.Future :
        record.ExpiresAtUtc is { } expiry && now >= expiry ? AgreementCalendarStatus.Expired : AgreementCalendarStatus.Active;
    /// <summary>Returns confidentiality state; dates never implicitly release obligations.</summary>
    public static ConfidentialityObligationStatus ObligationStatus(NdaRecord record, DateTimeOffset now) =>
        record.SurvivalKind == NdaSurvivalKind.Unknown || record.SurvivalKind == NdaSurvivalKind.Finite &&
        (record.SurvivalEndsAtUtc is null || now >= record.SurvivalEndsAtUtc)
        ? ConfidentialityObligationStatus.ReviewRequired : ConfidentialityObligationStatus.Protected;
}
