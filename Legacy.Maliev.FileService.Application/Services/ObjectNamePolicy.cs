using System.Text;
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
    public string BuildFinalObjectName(string? path, string fileName, Guid uploadId) =>
        BuildFinalObjectName(path, fileName, uploadId, out _);

    internal string BuildFinalObjectName(string? path, string fileName, Guid uploadId, out string normalizedObjectName)
    {
        var safeFileName = Path.GetFileName(fileName?.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName is "." or "..")
        {
            throw new FileUploadValidationException("File name is required");
        }

        // Keep the existing replay fingerprint's separator aliases on one destination.
        var customPrefix = string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim('/');
        var prefix = customPrefix is null
            ? BuildLegacyDefaultPrefix(uploadId)
            : NormalizeObjectName(customPrefix, allowTrailingSlash: true);
        var finalName = NormalizeObjectName($"{prefix}/{safeFileName}", allowTrailingSlash: false);
        normalizedObjectName = finalName.ToLowerInvariant();
        if (customPrefix is not null)
        {
            if (path is not null && path.Any(char.IsControl))
                throw new FileUploadValidationException("Object name is invalid");

            // Preserve prefix padding in storage identity, while callers guard the canonical safety name.
            var leadingPaddingLength = customPrefix.Length - customPrefix.TrimStart().Length;
            var trailingPaddingStart = customPrefix.TrimEnd().Length;
            var prefixBoundary = finalName.LastIndexOf('/');
            finalName = customPrefix[..leadingPaddingLength] + finalName.Insert(prefixBoundary, customPrefix[trailingPaddingStart..]);
        }

        return RequireObjectNameSize(finalName.ToLowerInvariant());
    }

    /// <summary>Builds a non-public quarantine object name.</summary>
    public string BuildQuarantineObjectName(Guid operationId, string finalObjectName) =>
        RequireObjectNameSize(NormalizeObjectName($"{options.QuarantinePrefix}/{operationId:N}/{finalObjectName}", allowTrailingSlash: false));

    /// <summary>Validates an existing object name without changing its case.</summary>
    public string RequireObjectName(string objectName) => NormalizeObjectName(objectName, allowTrailingSlash: false);

    private static string RequireObjectNameSize(string objectName)
    {
        // The GCS UTF-8 byte limit also bounds the journals' 1024-character columns.
        if (Encoding.UTF8.GetByteCount(objectName) > 1024)
            throw new FileUploadValidationException("Object name is too long");

        return objectName;
    }

    private string BuildLegacyDefaultPrefix(Guid uploadId)
    {
        var date = timeProvider.GetLocalNow();
        return $"uploads/{date.Year}-{date.Month}-{date.Day}/{uploadId}";
    }

    internal static string NormalizeObjectName(string value, bool allowTrailingSlash)
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
