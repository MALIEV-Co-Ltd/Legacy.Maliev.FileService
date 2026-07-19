using System.IO.Pipelines;
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
            now.Subtract(_options.OperationLeaseTimeout),
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
                upload.ModifiedAt = timeProvider.GetUtcNow();
                uint recoveryClaimVersion;
                try
                {
                    recoveryClaimVersion = await repository.SaveCleanupStateAsync(upload, stored.Version, cancellationToken);
                }
                catch (InstantQuoteConcurrencyException)
                {
                    continue;
                }
                await ReconcileRecoverableAsync(upload, recoveryClaimVersion, cancellationToken);
                continue;
            }
            if (upload.State is InstantQuoteWorkflowState.PayloadTooLarge or InstantQuoteWorkflowState.InvalidRequest &&
                upload.GcsGeneration is null)
            {
                upload.ModifiedAt = timeProvider.GetUtcNow();
                uint discoveryClaimVersion;
                try
                {
                    discoveryClaimVersion = await repository.SaveCleanupStateAsync(upload, stored.Version, cancellationToken);
                }
                catch (InstantQuoteConcurrencyException)
                {
                    continue;
                }
                await DiscoverAndDeleteRejectedAsync(upload, discoveryClaimVersion, cancellationToken);
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
        InstantQuoteUploadFile upload,
        uint claimedVersion,
        CancellationToken cancellationToken)
    {
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
                await repository.SaveCleanupStateAsync(upload, claimedVersion, cancellationToken);
                return;
            }

            upload.GcsGeneration = metadata.Generation;
            if (metadata.SizeBytes <= 0 || metadata.SizeBytes > InstantQuoteFileContract.MaximumUploadBytes)
            {
                upload.State = InstantQuoteWorkflowState.Failed;
                upload.ModifiedAt = timeProvider.GetUtcNow();
                await repository.SaveCleanupStateAsync(upload, claimedVersion, cancellationToken);
                return;
            }

            var outcome = await ScanGenerationAsync(metadata, linked.Token);
            var matches = outcome.SizeBytes == metadata.SizeBytes &&
                string.Equals(outcome.Sha256, metadata.Sha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(outcome.Sha256, upload.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            if (matches && outcome.Result == InstantQuoteScanResult.Clean)
            {
                try
                {
                    InstantQuoteContentSignaturePolicy.Validate(
                        upload.ValidatedExtension,
                        outcome.Prefix,
                        outcome.SizeBytes);
                    upload.ActualSha256 = outcome.Sha256;
                    upload.ActualSizeBytes = outcome.SizeBytes;
                    upload.State = InstantQuoteWorkflowState.Clean;
                }
                catch (InstantQuoteUnsafeContentException)
                {
                    upload.State = InstantQuoteWorkflowState.Failed;
                }
            }
            else if (outcome.Result != InstantQuoteScanResult.Unavailable)
            {
                upload.State = InstantQuoteWorkflowState.Failed;
            }
            upload.ModifiedAt = timeProvider.GetUtcNow();
            await repository.SaveCleanupStateAsync(upload, claimedVersion, cancellationToken);
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

    private async Task<RecoveryScanOutcome> ScanGenerationAsync(
        InstantQuoteObjectMetadata metadata,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        await using var reader = pipe.Reader.AsStream(leaveOpen: true);
        await using var writer = pipe.Writer.AsStream(leaveOpen: true);
        await using var hashing = new BoundedHashingReadStream(reader);
        await using var prefix = new RecoveryPrefixStream(hashing, 4096);
        var download = DownloadAsync();
        var scan = scanner.ScanAsync(prefix, cancellationToken);
        try
        {
            await Task.WhenAll(download, scan);
            if (!hashing.IsComplete)
            {
                throw new InstantQuoteDependencyUnavailableException("The recovery scanner did not consume the complete generation.");
            }
            return new(scan.Result, prefix.Prefix.ToArray(), hashing.Sha256, hashing.BytesRead);
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }

        async Task DownloadAsync()
        {
            Exception? error = null;
            try
            {
                await storage.DownloadGenerationAsync(
                    metadata.Bucket, metadata.ObjectName, metadata.Generation, writer, cancellationToken);
            }
            catch (Exception exception)
            {
                error = exception;
                throw;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(error);
            }
        }
    }

    private async Task DiscoverAndDeleteRejectedAsync(
        InstantQuoteUploadFile upload,
        uint claimedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.CleanupTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var metadata = await storage.GetMetadataAsync(
                upload.TemporaryBucket, upload.TemporaryObjectName, linked.Token);
            if (metadata is null)
            {
                return;
            }
            upload.GcsGeneration = metadata.Generation;
            await storage.DeleteGenerationAsync(metadata.Bucket, metadata.ObjectName, metadata.Generation, linked.Token);
            upload.GcsGeneration = null;
            upload.ModifiedAt = timeProvider.GetUtcNow();
            await repository.SaveCleanupStateAsync(upload, claimedVersion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Rejected temporary generation discovery failed for upload {UploadId} in session {SessionId}",
                upload.Id,
                upload.SessionId);
        }
    }

    private sealed class RecoveryPrefixStream(Stream source, int maximumPrefixBytes) : Stream
    {
        private readonly MemoryStream prefix = new(maximumPrefixBytes);
        public ReadOnlySpan<byte> Prefix => prefix.GetBuffer().AsSpan(0, checked((int)prefix.Length));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            var remaining = maximumPrefixBytes - checked((int)prefix.Length);
            if (remaining > 0 && read > 0) prefix.Write(buffer.Span[..Math.Min(read, remaining)]);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await source.DisposeAsync(); await prefix.DisposeAsync(); GC.SuppressFinalize(this); }
    }

    private sealed record RecoveryScanOutcome(InstantQuoteScanResult Result, byte[] Prefix, string Sha256, long SizeBytes);
}
