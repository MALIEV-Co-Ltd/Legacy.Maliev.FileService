using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Fail-closed legacy storage boundary used by write-disabled runtimes.</summary>
public sealed class DisabledObjectStorage : IObjectStorage
{
    private static Exception Unavailable() =>
        new MalwareScannerUnavailableException("Legacy file writes are disabled.");

    /// <inheritdoc />
    public Task UploadAsync(string bucket, string objectName, string contentType, Stream content, CancellationToken cancellationToken) =>
        Task.FromException(Unavailable());

    /// <inheritdoc />
    public Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
        Task.FromException<bool>(Unavailable());

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
        Task.FromException<bool>(Unavailable());

    /// <inheritdoc />
    public Task<long?> GetSizeAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
        Task.FromException<long?>(Unavailable());

    /// <inheritdoc />
    public Task<Uri> CreateSignedReadUriAsync(string bucket, string objectName, TimeSpan duration, CancellationToken cancellationToken) =>
        Task.FromException<Uri>(Unavailable());
}

/// <summary>Fail-closed legacy scanner boundary used by write-disabled runtimes.</summary>
public sealed class DisabledFileSafetyScanner : IFileSafetyScanner
{
    /// <inheritdoc />
    public Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new FileSafetyResult(FileSafetyVerdict.Unavailable));
}

/// <summary>Fail-closed instant-quotation storage boundary used by write-disabled runtimes.</summary>
public sealed class DisabledInstantQuoteObjectStorage : IInstantQuoteObjectStorage
{
    private static Exception Unavailable() =>
        new InstantQuoteDependencyUnavailableException("Instant quotation file writes are disabled.");

    /// <inheritdoc />
    public Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(string bucket, string objectName, Stream content, string expectedSha256, CancellationToken cancellationToken) =>
        Task.FromException<InstantQuoteObjectMetadata>(Unavailable());

    /// <inheritdoc />
    public Task<InstantQuoteObjectMetadata?> GetMetadataAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
        Task.FromException<InstantQuoteObjectMetadata?>(Unavailable());

    /// <inheritdoc />
    public Task DownloadGenerationAsync(string bucket, string objectName, long generation, Stream destination, CancellationToken cancellationToken) =>
        Task.FromException(Unavailable());

    /// <inheritdoc />
    public Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(string sourceBucket, string sourceObjectName, long sourceGeneration, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
        Task.FromException<InstantQuoteObjectMetadata>(Unavailable());

    /// <inheritdoc />
    public Task DeleteGenerationAsync(string bucket, string objectName, long generation, CancellationToken cancellationToken) =>
        Task.FromException(Unavailable());
}

/// <summary>Fail-closed instant-quotation scanner boundary used by write-disabled runtimes.</summary>
public sealed class DisabledInstantQuoteFileSafetyScanner : IInstantQuoteFileSafetyScanner
{
    /// <inheritdoc />
    public Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) =>
        Task.FromResult(InstantQuoteScanResult.Unavailable);
}
