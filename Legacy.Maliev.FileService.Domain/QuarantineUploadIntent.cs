namespace Legacy.Maliev.FileService.Domain;

/// <summary>Non-expiring private upload authority established before the first provider effect; not scan evidence.</summary>
public sealed class QuarantineUploadIntent
{
    /// <summary>Gets or sets the stable per-object operation identity.</summary>
    public Guid OperationId { get; set; }
    /// <summary>Gets or sets the enclosing request operation identity.</summary>
    public Guid ParentOperationId { get; set; }
    /// <summary>Gets or sets the exact private bucket.</summary>
    public string Bucket { get; set; } = string.Empty;
    /// <summary>Gets or sets the exact private quarantine object name.</summary>
    public string ObjectName { get; set; } = string.Empty;
    /// <summary>Gets or sets caller-declared content type, not independently certified content.</summary>
    public string ContentType { get; set; } = string.Empty;
    /// <summary>Gets or sets caller-declared byte count, not a complete-scan assertion.</summary>
    public long DeclaredSize { get; set; }
    /// <summary>Gets or sets the acknowledged immutable generation; null means no acknowledgment.</summary>
    public long? AcknowledgedGeneration { get; set; }
    /// <summary>Gets or sets the last durable stage.</summary>
    public string State { get; set; } = "Pending";
    /// <summary>Gets or sets the original creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the last checkpoint time.</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
