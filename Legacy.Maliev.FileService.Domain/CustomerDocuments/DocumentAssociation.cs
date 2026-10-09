namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Defines DocumentResourceKind values for customer document evidence.</summary>
public enum DocumentResourceKind
{
    /// <summary>Customer document evidence state.</summary>
    Customer,
    /// <summary>Order document evidence state.</summary>
    Order,
    /// <summary>Quotation document evidence state.</summary>
    Quotation,
    /// <summary>Invoice document evidence state.</summary>
    Invoice,
    /// <summary>Replacement document evidence state.</summary>
    Replacement,
    /// <summary>Shipment document evidence state.</summary>
    Shipment,
}
/// <summary>Captures an owner-confirmed resource association for one exact version.</summary>
public sealed class DocumentAssociation
{
    /// <summary>Gets or sets the stored VersionId evidence value.</summary>
    public Guid VersionId { get; set; }
    /// <summary>Gets or sets the stored Kind evidence value.</summary>
    public DocumentResourceKind Kind { get; set; }
    /// <summary>Gets or sets the stored ResourceId evidence value.</summary>
    public int ResourceId { get; set; }
    /// <summary>Gets or sets the stored CustomerId evidence value.</summary>
    public int CustomerId { get; set; }
}
