namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Retains append-only document actions and their authoritative actor.</summary>
public sealed class DocumentAudit
{
    /// <summary>Gets or sets the stored Id evidence value.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the stored DocumentId evidence value.</summary>
    public Guid DocumentId { get; set; }
    /// <summary>Gets or sets the stored VersionId evidence value.</summary>
    public Guid? VersionId { get; set; }
    /// <summary>Gets or sets the stored ActorSubject evidence value.</summary>
    public string ActorSubject { get; set; } = "";
    /// <summary>Gets or sets the stored AtUtc evidence value.</summary>
    public DateTimeOffset AtUtc { get; set; }
    /// <summary>Gets or sets the stored Action evidence value.</summary>
    public string Action { get; set; } = "";
    /// <summary>Gets or sets the stored Reason evidence value.</summary>
    public string? Reason { get; set; }
    /// <summary>Gets or sets the stored Revision evidence value.</summary>
    public long Revision { get; set; }
}
