using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Coordinates private quarantine, scanning, promotion, persistence, and signing.</summary>
public sealed class FileApplicationService(
    IObjectStorage storage,
    IFileSafetyScanner scanner,
    IUploadRepository repository,
    ObjectNamePolicy names,
    IOptions<FileStorageOptions> options,
    LegacyFileRuntimeGate runtimeGate,
    ILogger<FileApplicationService> logger) : IFileService
{
    /// <summary>Maximum aggregate size preserved from the legacy controller.</summary>
    public const long MaximumUploadBytes = 200L * 1024L * 1024L;
    /// <summary>Bounded multipart envelope allowance above the aggregate file limit.</summary>
    public const long MaximumRequestBytes = MaximumUploadBytes + (1L * 1024L * 1024L);

    /// <inheritdoc />
    public async Task<UploadResultResponse> UploadAsync(
        string bucket,
        string? path,
        IReadOnlyList<IUploadFile> files,
        CancellationToken cancellationToken) =>
        await UploadAsync(bucket, path, files, Guid.NewGuid(), cancellationToken);

    /// <inheritdoc />
    public async Task<UploadResultResponse> UploadAsync(
        string bucket,
        string? path,
        IReadOnlyList<IUploadFile> files,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        runtimeGate.EnsureWritesEnabled();
        names.RequireBucket(bucket);
        ValidateFiles(files);

        var promoted = new List<(string Bucket, string ObjectName)>();
        var quarantined = new List<(string Bucket, string ObjectName)>();
        var uploads = new List<Upload>(files.Count);
        var metadataCommitAttempted = false;

        try
        {
            foreach (var file in files)
            {
                var finalName = names.BuildFinalObjectName(path, file.FileName, operationId);
                var quarantineName = names.BuildQuarantineObjectName(operationId, finalName);
                await using (var content = file.OpenReadStream())
                {
                    await storage.UploadAsync(bucket, quarantineName, file.ContentType, content, cancellationToken);
                }

                quarantined.Add((bucket, quarantineName));
                var scan = await scanner.ScanAsync(file, cancellationToken);
                if (scan.Verdict == FileSafetyVerdict.Infected)
                {
                    logger.LogWarning("Rejected malware upload for bucket {Bucket}; threat {Threat}", bucket, scan.ThreatName ?? "unknown");
                    throw new MalwareDetectedException("Uploaded file contains malware");
                }

                if (scan.Verdict != FileSafetyVerdict.Clean)
                {
                    logger.LogWarning("Rejected upload because malware scanning was unavailable for bucket {Bucket}", bucket);
                    throw new MalwareScannerUnavailableException("Malware scanning is unavailable");
                }

                if (!await storage.MoveAsync(bucket, quarantineName, bucket, finalName, cancellationToken))
                {
                    throw new InvalidOperationException("Could not promote the scanned file");
                }

                quarantined.Remove((bucket, quarantineName));
                promoted.Add((bucket, finalName));
                uploads.Add(new Upload
                {
                    Bucket = bucket,
                    ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    Name = finalName,
                    Size = file.Length,
                });
            }

            metadataCommitAttempted = true;
            await repository.AddRangeAsync(uploads, cancellationToken);
            var duration = TimeSpan.FromHours(Math.Clamp(options.Value.SignedUrlHours, 1, 168));
            var result = new List<UploadObjectResponse>(uploads.Count);
            foreach (var upload in uploads)
            {
                var uri = await storage.CreateSignedReadUriAsync(upload.Bucket, upload.Name, duration, cancellationToken);
                result.Add(new UploadObjectResponse(upload.Bucket, upload.Name, uri));
            }

            return new UploadResultResponse(result);
        }
        catch
        {
            await BestEffortCleanupAsync(metadataCommitAttempted ? quarantined : quarantined.Concat(promoted));
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<UploadResultResponse?> ReconcileUploadAsync(
        string bucket,
        string? path,
        IReadOnlyList<IUploadFile> files,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        runtimeGate.EnsureWritesEnabled();
        names.RequireBucket(bucket);
        ValidateFiles(files);
        var duration = TimeSpan.FromHours(Math.Clamp(options.Value.SignedUrlHours, 1, 168));
        var result = new List<UploadObjectResponse>(files.Count);
        foreach (var file in files)
        {
            var objectName = names.BuildFinalObjectName(path, file.FileName, operationId);
            if (!await repository.ExistsAsync(bucket, objectName, cancellationToken)
                || await storage.GetSizeAsync(bucket, objectName, cancellationToken) != file.Length) return null;
            var uri = await storage.CreateSignedReadUriAsync(bucket, objectName, duration, cancellationToken);
            result.Add(new UploadObjectResponse(bucket, objectName, uri));
        }
        return new UploadResultResponse(result);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        runtimeGate.EnsureWritesEnabled();
        names.RequireBucket(bucket);
        objectName = names.RequireObjectName(objectName);
        if (!await repository.ExistsAsync(bucket, objectName, cancellationToken) ||
            !await storage.DeleteAsync(bucket, objectName, cancellationToken))
        {
            return false;
        }

        await repository.DeleteAsync(bucket, objectName, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MoveAsync(
        string sourceBucket,
        string sourceObjectName,
        string destinationBucket,
        string destinationObjectName,
        CancellationToken cancellationToken)
    {
        runtimeGate.EnsureWritesEnabled();
        names.RequireBucket(sourceBucket);
        names.RequireBucket(destinationBucket);
        sourceObjectName = names.RequireObjectName(sourceObjectName);
        destinationObjectName = names.RequireObjectName(destinationObjectName);
        if (!await repository.ExistsAsync(sourceBucket, sourceObjectName, cancellationToken) ||
            !await storage.MoveAsync(sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken))
        {
            return false;
        }

        await repository.MoveAsync(sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<Uri?> GetSignedUrlAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        runtimeGate.EnsureWritesEnabled();
        names.RequireBucket(bucket);
        objectName = names.RequireObjectName(objectName);
        if (!await repository.ExistsAsync(bucket, objectName, cancellationToken))
        {
            return null;
        }

        var duration = TimeSpan.FromHours(Math.Clamp(options.Value.SignedUrlHours, 1, 168));
        return await storage.CreateSignedReadUriAsync(bucket, objectName, duration, cancellationToken);
    }

    private static void ValidateFiles(IReadOnlyList<IUploadFile> files)
    {
        if (files.Count == 0)
        {
            throw new FileUploadValidationException("Files are required");
        }

        if (files.Any(file => string.IsNullOrWhiteSpace(file.FileName) || file.Length <= 0))
        {
            throw new FileUploadValidationException("Every file must have a name and content");
        }

        if (files.Sum(file => file.Length) > MaximumUploadBytes)
        {
            throw new FileUploadValidationException("Total upload size cannot exceed 200 MB");
        }
    }

    private async Task BestEffortCleanupAsync(IEnumerable<(string Bucket, string ObjectName)> objects)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var item in objects)
        {
            try
            {
                await storage.DeleteAsync(item.Bucket, item.ObjectName, cleanup.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to clean up object {ObjectName} from bucket {Bucket}", item.ObjectName, item.Bucket);
            }
        }
    }
}
