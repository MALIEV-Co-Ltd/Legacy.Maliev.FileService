using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;

namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Application boundary for legacy file operations.</summary>
public interface IFileService
{
    /// <summary>Uploads, scans, promotes, records, and signs files.</summary>
    Task<UploadResultResponse> UploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, CancellationToken cancellationToken);
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
