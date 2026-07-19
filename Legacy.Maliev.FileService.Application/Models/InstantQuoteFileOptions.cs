namespace Legacy.Maliev.FileService.Application.Models;

/// <summary>Application limits and lifetimes for instant-quotation file intake.</summary>
public sealed class InstantQuoteFileOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "InstantQuoteFiles";

    /// <summary>Gets or sets whether the workflow is available in this runtime.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets whether this runtime may mutate workflow state or objects.</summary>
    public bool WritesEnabled { get; set; }

    /// <summary>Gets or sets the private bucket used for temporary upload generations.</summary>
    public string TemporaryBucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the distinct private bucket used for finalized objects.</summary>
    public string FinalBucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the lifetime of a newly created upload session.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Gets or sets the independent timeout used for temporary-object cleanup.</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets the inactivity interval after which an operation reservation may be recovered.</summary>
    public TimeSpan OperationLeaseTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the hard timeout for one active upload or finalization worker.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets whether background temporary-object cleanup is enabled.</summary>
    public bool CleanupEnabled { get; set; }

    /// <summary>Gets or sets the interval between cleanup sweeps.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the delay before a claimed or failed cleanup may be retried.</summary>
    public TimeSpan CleanupRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the grace period after session expiry before clean uploads are removed.</summary>
    public TimeSpan CleanupSessionExpiryGrace { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets the maximum number of temporary generations processed per sweep.</summary>
    public int CleanupBatchSize { get; set; } = 50;
}
