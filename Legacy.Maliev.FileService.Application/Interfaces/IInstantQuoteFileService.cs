using Legacy.Maliev.FileService.Application.Models;

namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Application boundary for the instant-quotation upload workflow.</summary>
public interface IInstantQuoteFileService
{
    /// <summary>Creates an owned, time-limited upload session.</summary>
    Task<CreateInstantQuoteSessionResponse> CreateInstantQuoteSessionAsync(
        InstantQuoteOwner owner,
        CancellationToken cancellationToken);

    /// <summary>Streams, validates, scans, and records one idempotent upload.</summary>
    Task<InstantQuoteFileResponse> UploadAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        string expectedSha256,
        Stream body,
        InstantQuoteUploadMetadata metadata,
        CancellationToken cancellationToken);

    /// <summary>Idempotently links selected clean files to a quotation request.</summary>
    Task<FinalizeInstantQuoteFilesResponse> FinalizeAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        FinalizeInstantQuoteFilesRequest request,
        CancellationToken cancellationToken);

    /// <summary>Idempotently removes a pre-finalization upload owned by the session.</summary>
    Task RemoveAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        Guid fileId,
        CancellationToken cancellationToken);
}

/// <summary>Base type for failures that have a stable public HTTP representation.</summary>
public abstract class InstantQuoteContractException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Raised when request metadata or content is invalid.</summary>
public sealed class InstantQuoteValidationException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when the session token does not authorize the requested session.</summary>
public sealed class InstantQuoteOwnershipException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when an idempotency key is replayed with a different request.</summary>
public sealed class InstantQuoteReplayConflictException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when an identical idempotent operation is still pending.</summary>
public sealed class InstantQuoteUploadInProgressException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when content is infected, malformed, or does not match its declared format.</summary>
public sealed class InstantQuoteUnsafeContentException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when actual uploaded bytes exceed the contract limit.</summary>
public sealed class InstantQuotePayloadTooLargeException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when the declared or inferred upload media type is unsupported.</summary>
public sealed class InstantQuoteUnsupportedMediaTypeException(string message) : InstantQuoteContractException(message);

/// <summary>Raised when a required storage, scan, or state dependency is unavailable.</summary>
public sealed class InstantQuoteDependencyUnavailableException : InstantQuoteContractException
{
    /// <summary>Creates a dependency-unavailable failure.</summary>
    public InstantQuoteDependencyUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates a dependency-unavailable failure with its internal cause.</summary>
    public InstantQuoteDependencyUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Raised when cancellation or dependency failure leaves an outcome requiring reconciliation.</summary>
public sealed class InstantQuoteAmbiguousOutcomeException : InstantQuoteContractException
{
    /// <summary>Creates an ambiguous-outcome failure.</summary>
    public InstantQuoteAmbiguousOutcomeException(string message) : base(message)
    {
    }

    /// <summary>Creates an ambiguous-outcome failure with its internal cause.</summary>
    public InstantQuoteAmbiguousOutcomeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
