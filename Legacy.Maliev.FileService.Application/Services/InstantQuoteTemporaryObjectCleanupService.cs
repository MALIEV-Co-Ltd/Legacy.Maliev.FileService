using System.Security.Cryptography;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Deletes only database-authorized temporary object generations.</summary>
public sealed class InstantQuoteTemporaryObjectCleanupService(
    IInstantQuoteCleanupRepository repository,
    IInstantQuoteObjectStorage storage,
    IInstantQuoteFileSafetyScanner scanner,
    IOptions<InstantQuoteFileOptions> options,
    TimeProvider timeProvider,
    ILogger<InstantQuoteTemporaryObjectCleanupService> logger)
{
    private readonly InstantQuoteFileOptions _options = options.Value;

    /// <summary>Runs one bounded cleanup sweep and returns the number of completed deletions.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.WritesEnabled || !_options.CleanupEnabled)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var candidates = await repository.GetTemporaryCleanupCandidatesAsync(
            now.Subtract(_options.CleanupSessionExpiryGrace),
            now.Subtract(_options.CleanupRetryDelay),
            _options.CleanupBatchSize,
            cancellationToken);
        var completed = 0;
        foreach (var stored in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var upload = stored.Upload;
            if (upload.State is InstantQuoteWorkflowState.Pending or InstantQuoteWorkflowState.Uploaded or
                InstantQuoteWorkflowState.Unknown)
            {
                await ReconcileRecoverableAsync(stored, cancellationToken);
                continue;
            }
            if (upload.GcsGeneration is null)
            {
                continue;
            }

            if (upload.State == InstantQuoteWorkflowState.Clean)
            {
                upload.State = InstantQuoteWorkflowState.Removed;
            }
            upload.ModifiedAt = timeProvider.GetUtcNow();

            uint claimVersion;
            try
            {
                claimVersion = await repository.SaveCleanupStateAsync(upload, stored.Version, cancellationToken);
            }
            catch (InstantQuoteConcurrencyException)
            {
                continue;
            }

            var generation = upload.GcsGeneration.Value;
            try
            {
                using var timeout = new CancellationTokenSource(_options.CleanupTimeout, timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                await storage.DeleteGenerationAsync(
                    upload.TemporaryBucket,
                    upload.TemporaryObjectName,
                    generation,
                    linked.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Temporary generation cleanup failed for upload {UploadId} in session {SessionId}",
                    upload.Id,
                    upload.SessionId);
                continue;
            }

            upload.GcsGeneration = null;
            upload.ModifiedAt = timeProvider.GetUtcNow();
            try
            {
                await repository.SaveCleanupStateAsync(upload, claimVersion, cancellationToken);
                completed++;
            }
            catch (InstantQuoteConcurrencyException exception)
            {
                logger.LogWarning(exception,
                    "Temporary cleanup completion raced for upload {UploadId} in session {SessionId}",
                    upload.Id,
                    upload.SessionId);
            }
        }

        return completed;
    }

    private async Task ReconcileRecoverableAsync(
        InstantQuoteStoredUpload stored,
        CancellationToken cancellationToken)
    {
        var upload = stored.Upload;
        try
        {
            using var timeout = new CancellationTokenSource(_options.CleanupTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var metadata = await storage.GetMetadataAsync(
                upload.TemporaryBucket,
                upload.TemporaryObjectName,
                linked.Token);
            if (metadata is null)
            {
                upload.State = InstantQuoteWorkflowState.Failed;
                upload.ModifiedAt = timeProvider.GetUtcNow();
                await repository.SaveCleanupStateAsync(upload, stored.Version, cancellationToken);
                return;
            }

            upload.GcsGeneration = metadata.Generation;
            if (metadata.SizeBytes <= 0 || metadata.SizeBytes > InstantQuoteFileContract.MaximumUploadBytes)
            {
                upload.State = InstantQuoteWorkflowState.Failed;
                upload.ModifiedAt = timeProvider.GetUtcNow();
                await repository.SaveCleanupStateAsync(upload, stored.Version, cancellationToken);
                return;
            }

            await using var content = new MemoryStream(checked((int)metadata.SizeBytes));
            await storage.DownloadGenerationAsync(
                metadata.Bucket,
                metadata.ObjectName,
                metadata.Generation,
                content,
                linked.Token);
            var bytes = content.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            content.Position = 0;
            var scan = await scanner.ScanAsync(content, linked.Token);
            var matches = bytes.LongLength == metadata.SizeBytes &&
                string.Equals(sha256, metadata.Sha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sha256, upload.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            if (matches && scan == InstantQuoteScanResult.Clean)
            {
                try
                {
                    InstantQuoteContentSignaturePolicy.Validate(
                        upload.ValidatedExtension,
                        bytes.AsSpan(0, Math.Min(bytes.Length, 4096)),
                        bytes.LongLength);
                    upload.ActualSha256 = sha256;
                    upload.ActualSizeBytes = bytes.LongLength;
                    upload.State = InstantQuoteWorkflowState.Clean;
                }
                catch (InstantQuoteUnsafeContentException)
                {
                    upload.State = InstantQuoteWorkflowState.Failed;
                }
            }
            else if (scan != InstantQuoteScanResult.Unavailable)
            {
                upload.State = InstantQuoteWorkflowState.Failed;
            }
            upload.ModifiedAt = timeProvider.GetUtcNow();
            await repository.SaveCleanupStateAsync(upload, stored.Version, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InstantQuoteConcurrencyException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Temporary generation reconciliation failed for upload {UploadId} in session {SessionId}",
                upload.Id,
                upload.SessionId);
        }
    }
}
