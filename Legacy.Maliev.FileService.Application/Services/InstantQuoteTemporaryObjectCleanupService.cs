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
            if (upload.GcsGeneration is null || upload.State is InstantQuoteWorkflowState.Pending or
                InstantQuoteWorkflowState.Uploaded or InstantQuoteWorkflowState.Unknown)
            {
                continue;
            }

            if (upload.State == InstantQuoteWorkflowState.Clean)
            {
                upload.State = InstantQuoteWorkflowState.Removed;
            }
            upload.ModifiedAt = now;

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
}
