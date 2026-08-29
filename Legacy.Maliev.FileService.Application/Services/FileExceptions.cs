namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Raised when a legacy upload request violates its input contract.</summary>
public sealed class FileUploadValidationException(string message) : Exception(message);

/// <summary>Raised when uploaded content is malicious.</summary>
public sealed class MalwareDetectedException(string message) : Exception(message);

/// <summary>Raised when no scanner can establish that uploaded content is clean.</summary>
public sealed class MalwareScannerUnavailableException(string message) : Exception(message);

/// <summary>Describes one object that an upload rollback could not remove.</summary>
public sealed record UploadCleanupFailure(string Bucket, string ObjectName, Exception Cause);

/// <summary>Raised when an upload fails and one or more promoted objects cannot be rolled back.</summary>
public sealed class UploadRollbackException : InvalidOperationException
{
    /// <summary>Initializes a rollback failure with the original upload failure and every failed cleanup.</summary>
    public UploadRollbackException(Exception uploadFailure, IReadOnlyCollection<UploadCleanupFailure> cleanupFailures)
        : base(
            BuildMessage(cleanupFailures),
            new AggregateException(
                new[] { uploadFailure }.Concat(cleanupFailures.Select(failure => failure.Cause))))
    {
        UploadFailure = uploadFailure;
        CleanupFailures = cleanupFailures.ToArray();
    }

    /// <summary>Gets the failure that caused the upload workflow to roll back.</summary>
    public Exception UploadFailure { get; }

    /// <summary>Gets every object coordinate and cause that failed during rollback.</summary>
    public IReadOnlyList<UploadCleanupFailure> CleanupFailures { get; }

    private static string BuildMessage(IReadOnlyCollection<UploadCleanupFailure> cleanupFailures) =>
        $"Upload failed and rollback could not remove {cleanupFailures.Count} object(s): "
        + string.Join(", ", cleanupFailures.Select(failure => $"{failure.Bucket}/{failure.ObjectName}"));
}

/// <summary>Raised when a workflow key is reused for different upload content.</summary>
public sealed class UploadIdempotencyConflictException(string message) : Exception(message);
/// <summary>Raised while another executor owns the same upload workflow.</summary>
public sealed class UploadIdempotencyInProgressException(string message) : Exception(message);
/// <summary>Raised when storage or checkpoint state requires reconciliation.</summary>
public sealed class UploadOutcomeUnknownException(string message, Exception? innerException = null) : Exception(message, innerException);
/// <summary>Raised when durable upload replay protection cannot be reached.</summary>
public sealed class UploadIdempotencyUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
