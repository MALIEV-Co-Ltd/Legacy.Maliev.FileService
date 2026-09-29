namespace Legacy.Maliev.FileService.Domain;

/// <summary>Durable private coordinates and generation evidence for one storage move.</summary>
public sealed class StorageMoveJournal
{
    /// <summary>Gets or sets the stable operation identifier.</summary>
    public Guid OperationId { get; set; }
    /// <summary>Gets or sets whether a complete-file scan returned clean before the move.</summary>
    public bool ScanClean { get; set; }
    /// <summary>Gets or sets the source bucket.</summary>
    public string SourceBucket { get; set; } = string.Empty;
    /// <summary>Gets or sets the source object name.</summary>
    public string SourceObjectName { get; set; } = string.Empty;
    /// <summary>Gets or sets the observed immutable source generation.</summary>
    public long SourceGeneration { get; set; }
    /// <summary>Gets or sets the destination bucket.</summary>
    public string DestinationBucket { get; set; } = string.Empty;
    /// <summary>Gets or sets the destination object name.</summary>
    public string DestinationObjectName { get; set; } = string.Empty;
    /// <summary>Gets or sets the copied destination generation, if acknowledged.</summary>
    public long? DestinationGeneration { get; set; }
    /// <summary>Gets or sets the last confirmed stage.</summary>
    public string State { get; set; } = "SourceObserved";
    /// <summary>Gets or sets creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the last update time.</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
