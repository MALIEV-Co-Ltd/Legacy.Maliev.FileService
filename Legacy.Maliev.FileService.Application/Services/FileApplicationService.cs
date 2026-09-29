using System.Security.Cryptography;
using System.Text;
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
    IStorageMoveJournal moveJournal,
    ObjectNamePolicy names,
    IOptions<FileStorageOptions> options,
    LegacyFileRuntimeGate runtimeGate,
    ILogger<FileApplicationService> logger) : IFileService
{
    /// <summary>Maximum aggregate size accepted by the edge-facing upload workflow.</summary>
    public const long MaximumUploadBytes = 100L * 1024L * 1024L;
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

        var promoted = new List<(string Bucket, string ObjectName, Guid MoveId)>();
        var quarantined = new List<(string Bucket, string ObjectName, long Generation)>();
        var uploads = new List<Upload>(files.Count);

        try
        {
            foreach (var file in files)
            {
                var finalName = names.BuildFinalObjectName(path, file.FileName, operationId);
                var quarantineName = names.BuildQuarantineObjectName(operationId, finalName);
                long quarantineGeneration;
                await using (var content = file.OpenReadStream())
                {
                    quarantineGeneration = await storage.UploadGenerationAsync(bucket, quarantineName, file.ContentType, content, cancellationToken);
                }

                quarantined.Add((bucket, quarantineName, quarantineGeneration));
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

                var moveId = MoveId(operationId, finalName);
                if (!await storage.MoveJournaledAsync(moveId, quarantineGeneration, true, bucket, quarantineName, bucket, finalName, cancellationToken))
                {
                    throw new UploadOutcomeUnknownException("Scanned quarantine promotion requires reconciliation.");
                }

                quarantined.Remove((bucket, quarantineName, quarantineGeneration));
                promoted.Add((bucket, finalName, moveId));
                uploads.Add(new Upload
                {
                    Bucket = bucket,
                    ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    Name = finalName,
                    Size = file.Length,
                });
            }

            var duration = TimeSpan.FromHours(Math.Clamp(options.Value.SignedUrlHours, 1, 168));
            var result = new List<UploadObjectResponse>(uploads.Count);
            foreach (var upload in uploads)
            {
                var uri = await storage.CreateSignedReadUriAsync(upload.Bucket, upload.Name, duration, cancellationToken);
                result.Add(new UploadObjectResponse(upload.Bucket, upload.Name, uri));
            }

            await repository.AddRangeAsync(uploads, cancellationToken);
            foreach (var move in promoted)
            {
                try { await moveJournal.MetadataCommittedAsync(move.MoveId, cancellationToken); }
                catch (Exception exception)
                {
                    throw new UploadOutcomeUnknownException("Committed metadata checkpoint requires reconciliation.", exception);
                }
            }

            return new UploadResultResponse(result);
        }
        catch (UploadOutcomeUnknownException)
        {
            // The move may have committed on either side of an interrupted GCS response.
            // Keep quarantine and any copied object available for explicit reconciliation.
            logger.LogWarning("Upload storage outcome requires reconciliation for operation {OperationId}", operationId);
            throw;
        }
        catch (Exception uploadFailure)
        {
            if (promoted.Count != 0)
            {
                foreach (var move in promoted) await MarkMoveUnknownAsync(move.MoveId);
                logger.LogWarning("Upload storage outcome requires reconciliation for operation {OperationId}", operationId);
                throw new UploadOutcomeUnknownException("Upload promotion requires reconciliation.", uploadFailure);
            }

            var cleanupFailures = await CleanupAsync(quarantined, operationId);
            if (cleanupFailures.Count != 0)
            {
                throw new UploadRollbackException(uploadFailure, cleanupFailures);
            }

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
            var move = await moveJournal.FindAsync(MoveId(operationId, objectName), cancellationToken);
            if (move is null || !move.ScanClean || move.State is not ("SourceDeleted" or "MetadataCommitted" or "Unknown")
                || move.SourceBucket != bucket
                || move.SourceObjectName != names.BuildQuarantineObjectName(operationId, objectName)
                || move.SourceGeneration <= 0
                || move.DestinationBucket != bucket || move.DestinationObjectName != objectName
                || move.DestinationGeneration is not long destinationGeneration
                || !await repository.ExistsAsync(bucket, objectName, cancellationToken)) return null;
            var live = await storage.GetEvidenceAsync(bucket, objectName, cancellationToken);
            if (live is null || live.Generation != destinationGeneration || live.Size != file.Length) return null;
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
        var moveId = Guid.NewGuid();
        if (!await repository.ExistsAsync(sourceBucket, sourceObjectName, cancellationToken) ||
            !await storage.MoveJournaledAsync(moveId, null, true, sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken))
        {
            return false;
        }

        try { await repository.MoveAsync(sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken); }
        catch (Exception exception)
        {
            await MarkMoveUnknownAsync(moveId);
            logger.LogWarning("Storage move metadata outcome requires reconciliation for operation {OperationId}", moveId);
            throw new UploadOutcomeUnknownException("Storage move metadata requires reconciliation.", exception);
        }
        try { await moveJournal.MetadataCommittedAsync(moveId, cancellationToken); }
        catch (Exception exception)
        {
            throw new UploadOutcomeUnknownException("Committed metadata checkpoint requires reconciliation.", exception);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<Uri?> GetSignedUrlAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        runtimeGate.EnsureStorageEnabled();
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
            throw new FileUploadValidationException("Total upload size cannot exceed 100 MB");
        }
    }

    private static Guid MoveId(Guid operationId, string destinationName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}\n{destinationName}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async Task MarkMoveUnknownAsync(Guid moveId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await moveJournal.UnknownAsync(moveId, timeout.Token); }
        catch { /* The persisted last confirmed stage remains available to operators. */ }
    }

    private async Task<IReadOnlyList<UploadCleanupFailure>> CleanupAsync(
        IEnumerable<(string Bucket, string ObjectName, long Generation)> objects,
        Guid operationId)
    {
        var failures = new List<UploadCleanupFailure>();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var item in objects)
        {
            try
            {
                await storage.DeleteGenerationAsync(item.Bucket, item.ObjectName, item.Generation, cleanup.Token);
            }
            catch (Exception exception)
            {
                failures.Add(new UploadCleanupFailure(item.Bucket, item.ObjectName, exception));
                logger.LogWarning("Failed to clean up private quarantine generation for operation {OperationId}", operationId);
            }
        }

        return failures;
    }
}
