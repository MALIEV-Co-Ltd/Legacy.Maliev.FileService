namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Retains durable exclusive upload identity and outcome for reconciliation.</summary>
public sealed class ProtectedDocumentUploadCheckpoint
{
    /// <summary>Gets or sets the immutable operation identity.</summary>
    public Guid OperationId { get; set; }
    /// <summary>Gets or sets authoritative customer scope.</summary>
    public int CustomerId { get; set; }
    /// <summary>Gets or sets the authorized actor subject.</summary>
    public string Subject { get; set; } = "";
    /// <summary>Gets or sets the hashed idempotency identity.</summary>
    public string KeyHash { get; set; } = "";
    /// <summary>Gets or sets the complete request fingerprint.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Gets or sets the reserved document.</summary>
    public Guid DocumentId { get; set; }
    /// <summary>Gets or sets the reserved immutable version.</summary>
    public Guid VersionId { get; set; }
    /// <summary>Gets or sets the reserved serialized sequence.</summary>
    public int VersionNumber { get; set; }
    /// <summary>Gets or sets Pending, Unknown or Completed state.</summary>
    public string State { get; set; } = "Pending";
    /// <summary>Gets or sets the exact completed receipt revision.</summary>
    public long? CompletedRevision { get; set; }
}
/// <summary>Retains an authoritative legal hold without deleting originals.</summary>
public sealed class CustomerDocumentLegalHold
{
    /// <summary>Gets or sets the held document.</summary>
    public Guid DocumentId { get; set; }
    /// <summary>Gets or sets customer scope.</summary>
    public int CustomerId { get; set; }
    /// <summary>Gets or sets the hold authority reason.</summary>
    public string Reason { get; set; } = "";
}
