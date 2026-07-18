namespace Legacy.Maliev.FileService.Application.Models;

/// <summary>Application limits and lifetimes for instant-quotation file intake.</summary>
public sealed class InstantQuoteFileOptions
{
    /// <summary>Gets or sets the lifetime of a newly created upload session.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Gets or sets the independent timeout used for temporary-object cleanup.</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
