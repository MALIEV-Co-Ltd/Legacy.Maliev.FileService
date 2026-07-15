namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Raised when a legacy upload request violates its input contract.</summary>
public sealed class FileUploadValidationException(string message) : Exception(message);

/// <summary>Raised when uploaded content is malicious.</summary>
public sealed class MalwareDetectedException(string message) : Exception(message);

/// <summary>Raised when no scanner can establish that uploaded content is clean.</summary>
public sealed class MalwareScannerUnavailableException(string message) : Exception(message);
