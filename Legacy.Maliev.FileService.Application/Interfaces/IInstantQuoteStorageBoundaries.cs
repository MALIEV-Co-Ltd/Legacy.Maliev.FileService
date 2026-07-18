namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Immutable private-object metadata used by instant-quotation orchestration.</summary>
public sealed record InstantQuoteObjectMetadata(
    string Bucket,
    string ObjectName,
    long Generation,
    long SizeBytes,
    string Sha256);

/// <summary>Private object operations required by instant-quotation orchestration.</summary>
public interface IInstantQuoteObjectStorage
{
    /// <summary>Uploads a private temporary object without overwriting an existing object.</summary>
    Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(
        string bucket,
        string objectName,
        Stream content,
        string expectedSha256,
        CancellationToken cancellationToken);

    /// <summary>Gets metadata for an object used to reconcile an ambiguous operation.</summary>
    Task<InstantQuoteObjectMetadata?> GetMetadataAsync(
        string bucket,
        string objectName,
        CancellationToken cancellationToken);

    /// <summary>Streams an exact immutable generation into the destination.</summary>
    Task DownloadGenerationAsync(
        string bucket,
        string objectName,
        long generation,
        Stream destination,
        CancellationToken cancellationToken);

    /// <summary>Conditionally promotes an exact generation to a non-overwriting destination.</summary>
    Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(
        string sourceBucket,
        string sourceObjectName,
        long sourceGeneration,
        string destinationBucket,
        string destinationObjectName,
        CancellationToken cancellationToken);

    /// <summary>Conditionally deletes an exact object generation, whether temporary or finalized.</summary>
    Task DeleteGenerationAsync(
        string bucket,
        string objectName,
        long generation,
        CancellationToken cancellationToken);
}

/// <summary>Stable malware-scan classifications used by the application layer.</summary>
public enum InstantQuoteScanResult
{
    /// <summary>The complete object was scanned and accepted.</summary>
    Clean,
    /// <summary>The object is infected or otherwise unsafe.</summary>
    Unsafe,
    /// <summary>The scanner could not provide a trustworthy result.</summary>
    Unavailable,
}

/// <summary>Scans a complete streamed object without storage-SDK coupling.</summary>
public interface IInstantQuoteFileSafetyScanner
{
    /// <summary>Scans the supplied content stream.</summary>
    Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}
