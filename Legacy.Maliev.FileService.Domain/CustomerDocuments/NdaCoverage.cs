namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Retains immutable, explicit canonical scope for an exact NDA verification.</summary>
public sealed class NdaCoverage
{
    /// <summary>Gets or sets the immutable NDA record identity.</summary>
    public Guid NdaId { get; set; }
    /// <summary>Gets or sets the canonical customer.</summary>
    public int CustomerId { get; set; }
    /// <summary>Gets or sets the resource kind confirmed by its owner.</summary>
    public DocumentResourceKind Kind { get; set; }
    /// <summary>Gets or sets the owner-confirmed positive resource identity.</summary>
    public int ResourceId { get; set; }
}
