namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Raised when a legacy upload request violates its input contract.</summary>
public sealed class FileUploadValidationException(string message) : Exception(message);

/// <summary>Raised when uploaded content is malicious.</summary>
public sealed class MalwareDetectedException(string message) : Exception(message);

/// <summary>Raised when no scanner can establish that uploaded content is clean.</summary>
public sealed class MalwareScannerUnavailableException(string message) : Exception(message);

/// <summary>Raised when a workflow key is reused for different upload content.</summary>
public sealed class UploadIdempotencyConflictException(string message) : Exception(message);
/// <summary>Raised while another executor owns the same upload workflow.</summary>
public sealed class UploadIdempotencyInProgressException(string message) : Exception(message);
/// <summary>Raised when storage or checkpoint state requires reconciliation.</summary>
public sealed class UploadOutcomeUnknownException(string message, Exception? innerException = null) : Exception(message, innerException);
/// <summary>Raised when durable upload replay protection cannot be reached.</summary>
public sealed class UploadIdempotencyUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
