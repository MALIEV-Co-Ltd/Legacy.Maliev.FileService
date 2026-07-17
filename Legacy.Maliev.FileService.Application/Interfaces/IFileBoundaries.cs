using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;

namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Application boundary for legacy file operations.</summary>
public interface IFileService
{
    /// <summary>Uploads, scans, promotes, records, and signs files.</summary>
    Task<UploadResultResponse> UploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, CancellationToken cancellationToken);
    /// <summary>Runs an upload with a deterministic generation used for replay reconciliation.</summary>
    /// <returns>The clean uploaded objects and their signed read URIs.</returns>
    Task<UploadResultResponse> UploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, Guid operationId, CancellationToken cancellationToken);
    /// <summary>Reconstructs a completed response when every deterministic object has clean metadata.</summary>
    /// <returns>The recovered response, or <see langword="null"/> when reconciliation is incomplete.</returns>
    Task<UploadResultResponse?> ReconcileUploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, Guid operationId, CancellationToken cancellationToken);
    /// <summary>Deletes a clean object and its metadata.</summary>
    Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken);
    /// <summary>Moves a clean object and updates its metadata.</summary>
    Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken);
    /// <summary>Creates a read URL only for an object recorded as clean.</summary>
    Task<Uri?> GetSignedUrlAsync(string bucket, string objectName, CancellationToken cancellationToken);
}

/// <summary>Private object-storage boundary.</summary>
public interface IObjectStorage
{
    /// <summary>Uploads an object into private quarantine.</summary>
    Task UploadAsync(string bucket, string objectName, string contentType, Stream content, CancellationToken cancellationToken);
    /// <summary>Moves an object between private locations.</summary>
    Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken);
    /// <summary>Deletes an object.</summary>
    Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken);
    /// <summary>Reads the current object size for reconciliation without downloading content.</summary>
    Task<long?> GetSizeAsync(string bucket, string objectName, CancellationToken cancellationToken);
    /// <summary>Creates a time-limited signed read URL.</summary>
    Task<Uri> CreateSignedReadUriAsync(string bucket, string objectName, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>Complete-file malware scanning boundary.</summary>
public interface IFileSafetyScanner
{
    /// <summary>Scans all bytes from a fresh file stream.</summary>
    Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken cancellationToken);
}

/// <summary>Clean-upload metadata persistence boundary.</summary>
public interface IUploadRepository
{
    /// <summary>Adds metadata for promoted clean objects atomically.</summary>
    Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken cancellationToken);
    /// <summary>Tests whether clean metadata exists.</summary>
    Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken cancellationToken);
    /// <summary>Deletes clean metadata.</summary>
    Task DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken);
    /// <summary>Moves clean metadata.</summary>
    Task MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken);
}

/// <summary>Durable fenced state for replay-safe upload workflows.</summary>
public interface IUploadIdempotencyStore
{
    /// <summary>Atomically acquires or reads a durable upload checkpoint.</summary>
    Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, CancellationToken cancellationToken);
    /// <summary>Renews fenced ownership when the reservation still owns the checkpoint.</summary>
    Task<bool> RenewAsync(string identity, string reservationId, CancellationToken cancellationToken);
    /// <summary>Persists the exact completed upload response.</summary>
    Task CompleteAsync(string identity, string fingerprint, string reservationId, UploadResultResponse response, CancellationToken cancellationToken);
    /// <summary>Retains an ambiguous outcome and any response already produced.</summary>
    Task MarkUnknownAsync(string identity, string reservationId, UploadResultResponse? response, CancellationToken cancellationToken);
    /// <summary>Releases a known failed and fully cleaned reservation.</summary>
    Task ReleaseAsync(string identity, string reservationId, CancellationToken cancellationToken);
}

/// <summary>Classifies the durable checkpoint observed during upload acquisition.</summary>
public enum UploadAcquireState
{
    /// <summary>The caller owns a new reservation.</summary>
    Acquired,
    /// <summary>An exact completed response can be replayed.</summary>
    Replay,
    /// <summary>The workflow identity is bound to different content.</summary>
    Conflict,
    /// <summary>Another executor currently owns the workflow.</summary>
    InProgress,
    /// <summary>The prior outcome must be reconciled.</summary>
    Unknown,
}
/// <summary>Returns acquisition state, fenced reservation identity, and optional replay response.</summary>
/// <param name="State">The durable checkpoint state.</param>
/// <param name="ReservationId">The fenced reservation identifier when available.</param>
/// <param name="Response">The exact completed response when available.</param>
public sealed record UploadAcquireResult(UploadAcquireState State, string? ReservationId = null, UploadResultResponse? Response = null);
