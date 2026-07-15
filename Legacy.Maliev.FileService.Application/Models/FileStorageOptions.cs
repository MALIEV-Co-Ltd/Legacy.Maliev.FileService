namespace Legacy.Maliev.FileService.Application.Models;

/// <summary>Configuration for secure legacy object storage.</summary>
public sealed class FileStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FileStorage";
    /// <summary>Gets or sets buckets accepted from legacy callers.</summary>
    public string[] AllowedBuckets { get; set; } = [];
    /// <summary>Gets or sets the private staging prefix.</summary>
    public string QuarantinePrefix { get; set; } = "_quarantine";
    /// <summary>Gets or sets signed URL lifetime in hours, capped at seven days.</summary>
    public int SignedUrlHours { get; set; } = 168;
}

/// <summary>Configuration for the ClamAV INSTREAM scanner.</summary>
public sealed class MalwareScannerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MalwareScanner";
    /// <summary>Gets or sets the ClamAV service host. Empty means unavailable and uploads fail closed.</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>Gets or sets the ClamAV TCP port.</summary>
    public int Port { get; set; } = 3310;
    /// <summary>Gets or sets the connection and response timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
