namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Checks read access to an instant-quotation object bucket without mutating it.</summary>
public interface IInstantQuoteObjectStorageReadinessProbe
{
    /// <summary>Checks metadata access to the supplied bucket.</summary>
    Task CheckBucketAsync(string bucket, CancellationToken cancellationToken);
}

/// <summary>Checks whether the instant-quotation malware scanner can accept work.</summary>
public interface IInstantQuoteScannerReadinessProbe
{
    /// <summary>Checks scanner availability without sending file content.</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}
