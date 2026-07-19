namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Checks whether the instant-quotation malware scanner can accept work.</summary>
public interface IInstantQuoteScannerReadinessProbe
{
    /// <summary>Checks scanner availability without sending file content.</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}
