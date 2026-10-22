using System.Text.Json.Serialization;
namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Defines DocumentKind values for customer document evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DocumentKind>))]
public enum DocumentKind
{
    /// <summary>Nda document evidence state.</summary>
    Nda,
    /// <summary>Corporate document evidence state.</summary>
    Corporate,
    /// <summary>BillingInstruction document evidence state.</summary>
    BillingInstruction,
    /// <summary>Shipment document evidence state.</summary>
    Shipment,
    /// <summary>Release document evidence state.</summary>
    Release,
    /// <summary>Acceptance document evidence state.</summary>
    Acceptance,
    /// <summary>Evidence document evidence state.</summary>
    Evidence,
}
/// <summary>Defines DocumentVisibility values for customer document evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DocumentVisibility>))]
public enum DocumentVisibility
{
    /// <summary>Internal document evidence state.</summary>
    Internal,
    /// <summary>Customer document evidence state.</summary>
    Customer,
}
/// <summary>Defines VerificationStatus values for customer document evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationStatus>))]
public enum VerificationStatus
{
    /// <summary>PendingVerification document evidence state.</summary>
    PendingVerification,
    /// <summary>Verified document evidence state.</summary>
    Verified,
    /// <summary>Rejected document evidence state.</summary>
    Rejected,
}
/// <summary>Stores immutable customer ownership and document classification.</summary>
public sealed class CustomerDocument
{
    /// <summary>Gets or sets the stored Id evidence value.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the stored CustomerId evidence value.</summary>
    public int CustomerId { get; set; }
    /// <summary>Gets or sets the stored Kind evidence value.</summary>
    public DocumentKind Kind { get; set; }
    /// <summary>Gets or sets the stored Title evidence value.</summary>
    public string Title { get; set; } = "";
    /// <summary>Gets or sets the stored Visibility evidence value.</summary>
    public DocumentVisibility Visibility { get; set; }
    /// <summary>Gets or sets the stored ArchivedAtUtc evidence value.</summary>
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    /// <summary>Gets or sets the stored Revision evidence value.</summary>
    public long Revision { get; set; } = 1;
}
