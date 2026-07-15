using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Validates legacy bucket and object names and prevents path traversal.</summary>
public sealed class ObjectNamePolicy(IOptions<FileStorageOptions> options, TimeProvider timeProvider)
{
    private readonly FileStorageOptions options = options.Value;

    /// <summary>Validates and returns an allowed bucket name.</summary>
    public string RequireBucket(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket) ||
            !options.AllowedBuckets.Contains(bucket, StringComparer.Ordinal))
        {
            throw new FileUploadValidationException("Bucket is not allowed");
        }

        return bucket;
    }

    /// <summary>Builds the legacy-compatible lower-case final object name.</summary>
    public string BuildFinalObjectName(string? path, string fileName, Guid uploadId)
    {
        var safeFileName = Path.GetFileName(fileName?.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName is "." or "..")
        {
            throw new FileUploadValidationException("File name is required");
        }

        var prefix = string.IsNullOrWhiteSpace(path)
            ? BuildLegacyDefaultPrefix(uploadId)
            : NormalizeObjectName(path, allowTrailingSlash: true);
        return NormalizeObjectName($"{prefix}/{safeFileName}", allowTrailingSlash: false).ToLowerInvariant();
    }

    /// <summary>Builds a non-public quarantine object name.</summary>
    public string BuildQuarantineObjectName(Guid operationId, string finalObjectName) =>
        NormalizeObjectName($"{options.QuarantinePrefix}/{operationId:N}/{finalObjectName}", allowTrailingSlash: false);

    /// <summary>Validates an existing object name without changing its case.</summary>
    public string RequireObjectName(string objectName) => NormalizeObjectName(objectName, allowTrailingSlash: false);

    private string BuildLegacyDefaultPrefix(Guid uploadId)
    {
        var date = timeProvider.GetUtcNow();
        return $"uploads/{date.Year}-{date.Month}-{date.Day}/{uploadId}";
    }

    private static string NormalizeObjectName(string value, bool allowTrailingSlash)
    {
        var normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (allowTrailingSlash)
        {
            normalized = normalized.TrimEnd('/');
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Any(char.IsControl)))
        {
            throw new FileUploadValidationException("Object name is invalid");
        }

        return string.Join('/', segments);
    }
}
