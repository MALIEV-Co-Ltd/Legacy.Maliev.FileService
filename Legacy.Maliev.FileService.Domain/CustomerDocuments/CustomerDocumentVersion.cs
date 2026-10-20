namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Stores immutable exact-version digest and uploader evidence.</summary>
public sealed class CustomerDocumentVersion
{
    /// <summary>Gets or sets the stored Id evidence value.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the stored DocumentId evidence value.</summary>
    public Guid DocumentId { get; set; }
    /// <summary>Gets or sets the stored CustomerId evidence value.</summary>
    public int CustomerId { get; set; }
    /// <summary>Gets or sets the stored Kind evidence value.</summary>
    public DocumentKind Kind { get; set; }
    /// <summary>Gets or sets the stored VersionNumber evidence value.</summary>
    public int VersionNumber { get; set; }
    /// <summary>Gets or sets the stored ContentSha256 evidence value.</summary>
    public string ContentSha256 { get; set; } = "";
    /// <summary>Gets or sets the stored ActorSubject evidence value.</summary>
    public string ActorSubject { get; set; } = "";
    /// <summary>Gets or sets the stored CreatedAtUtc evidence value.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>Gets or sets the stored Revision evidence value.</summary>
    public long Revision { get; set; } = 1;
    /// <summary>Gets or sets the irreversible finalization seal for the captured association set.</summary>
    public bool AssociationsSealed { get; set; }
    /// <summary>Gets or sets the private immutable storage bucket.</summary>
    public string StorageBucket { get; set; } = "";
    /// <summary>Gets or sets the private registry-reserved object identity.</summary>
    public string StorageObjectName { get; set; } = "";
    /// <summary>Gets or sets the exact immutable clean object generation.</summary>
    public long StorageGeneration { get; set; }
    /// <summary>Gets or sets the exact byte length.</summary>
    public long ContentSize { get; set; }
    /// <summary>Gets or sets the detected supported content type.</summary>
    public string ContentType { get; set; } = "";
    /// <summary>Gets or sets the validated attachment filename.</summary>
    public string OriginalFileName { get; set; } = "";
    /// <summary>Gets or sets the clean scan operation identity.</summary>
    public Guid ScanOperationId { get; set; }
    /// <summary>Gets or sets the exact generation certified by the complete-file scan.</summary>
    public long ScanSourceGeneration { get; set; }
}
/// <summary>Retains an append-only verification decision for an exact byte version.</summary>
public sealed class DocumentVerificationEvidence
{
    /// <summary>Gets or sets the stored VersionId evidence value.</summary>
    public Guid VersionId { get; set; }
    /// <summary>Gets or sets the stored Revision evidence value.</summary>
    public long Revision { get; set; }
    /// <summary>Gets or sets the stored Status evidence value.</summary>
    public VerificationStatus Status { get; set; }
    /// <summary>Gets or sets the stored VerifiedBySubject evidence value.</summary>
    public string? VerifiedBySubject { get; set; }
    /// <summary>Gets or sets the stored VerifiedAtUtc evidence value.</summary>
    public DateTimeOffset? VerifiedAtUtc { get; set; }
}
