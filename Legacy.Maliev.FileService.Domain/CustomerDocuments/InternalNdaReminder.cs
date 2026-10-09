using System.Text.Json.Serialization;
namespace Legacy.Maliev.FileService.Domain.CustomerDocuments;
/// <summary>Defines durable in-app worklist states without a send transport.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InternalNdaReminderState>))]
public enum InternalNdaReminderState
{
    /// <summary>A reminder is due for responsible staff.</summary>
    Due,
    /// <summary>A past calendar evaluation was missed and needs attention.</summary>
    Missed,
    /// <summary>A newer immutable agreement cancels a stale future reminder.</summary>
    Cancelled,
}
/// <summary>Retains an employee-only in-app reminder for one exact renewal epoch.</summary>
public sealed class InternalNdaReminder
{
    /// <summary>Gets or sets the durable reminder identity.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the exact agreement record.</summary>
    public Guid NdaId { get; set; }
    /// <summary>Gets or sets the exact signed agreement version.</summary>
    public Guid VersionId { get; set; }
    /// <summary>Gets or sets the immutable renewal revision.</summary>
    public long RenewalRevision { get; set; }
    /// <summary>Gets or sets the configured lead day.</summary>
    public int LeadDays { get; set; }
    /// <summary>Gets or sets the verified active employee recipient.</summary>
    public string ResponsibleEmployeeSubject { get; set; } = "";
    /// <summary>Gets or sets the explicit calendar due instant converted to UTC.</summary>
    public DateTimeOffset DueAtUtc { get; set; }
    /// <summary>Gets or sets the first durable evaluation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>Gets or sets the durable processing state.</summary>
    public InternalNdaReminderState State { get; set; }
    /// <summary>Gets or sets the UTC cancellation audit instant.</summary>
    public DateTimeOffset? CancelledAtUtc { get; set; }
}
