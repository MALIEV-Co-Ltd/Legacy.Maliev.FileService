using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Orchestrates private, scanned instant-quotation file intake.</summary>
public sealed class InstantQuoteFileService : IInstantQuoteFileService
{
    private readonly IInstantQuoteFileRepository _repository;
    private readonly IInstantQuoteObjectStorage _storage;
    private readonly IInstantQuoteFileSafetyScanner _scanner;
    private readonly InstantQuoteFileOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the application workflow orchestrator.</summary>
    public InstantQuoteFileService(
        IInstantQuoteFileRepository repository,
        IInstantQuoteObjectStorage storage,
        IInstantQuoteFileSafetyScanner scanner,
        IOptions<InstantQuoteFileOptions> options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _storage = storage;
        _scanner = scanner;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<CreateInstantQuoteSessionResponse> CreateInstantQuoteSessionAsync(
        InstantQuoteOwner owner,
        CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        var token = RandomNumberGenerator.GetBytes(32);
        var now = _timeProvider.GetUtcNow();
        var session = new InstantQuoteUploadSession(
            Guid.NewGuid(),
            owner.PrincipalId,
            owner.IsAuthenticated,
            SHA256.HashData(token),
            now.Add(_options.SessionLifetime),
            now);
        await ExecuteDurableStateAsync(async () =>
        {
            await _repository.CreateSessionAsync(session, cancellationToken);
            return true;
        });
        return new CreateInstantQuoteSessionResponse(
            session.Id,
            Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            session.ExpiresAt,
            InstantQuoteFileContract.MaximumUploadBytes,
            InstantQuoteFileContract.MaximumFilesPerSession,
            InstantQuoteFileContract.SupportedExtensions);
    }

    /// <inheritdoc />
    public async Task<InstantQuoteFileResponse> UploadAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        string expectedSha256,
        Stream body,
        InstantQuoteUploadMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.OperationTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await UploadCoreAsync(
                sessionId, owner, token, idempotencyKey, expectedSha256, body, metadata, linked.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new InstantQuoteDependencyUnavailableException("The upload operation timed out and requires retry.", exception);
        }
    }

    private async Task<InstantQuoteFileResponse> UploadCoreAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        string expectedSha256,
        Stream body,
        InstantQuoteUploadMetadata metadata,
        CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        var headers = InstantQuoteFilePolicy.NormalizeHeaders(token, idempotencyKey, expectedSha256);
        var normalized = InstantQuoteFilePolicy.NormalizeFileMetadata(metadata.FileName, metadata.ContentType);
        var tokenHash = SHA256.HashData(DecodeBase64Url(headers.Token));
        var session = await ExecuteDurableStateAsync(() => _repository.VerifySessionAsync(
            sessionId, tokenHash, owner.PrincipalId, owner.IsAuthenticated, _timeProvider.GetUtcNow(), cancellationToken));
        if (session is null)
        {
            throw new InstantQuoteOwnershipException("The upload session could not be authorized.");
        }

        var idempotencyHash = SHA256.HashData(Encoding.UTF8.GetBytes(headers.IdempotencyKey));
        var fingerprint = HashText(string.Join('\n', sessionId.ToString("N"), normalized.Metadata.FileName,
            normalized.Metadata.ContentType, headers.ExpectedSha256));
        var fileId = Guid.NewGuid();
        var temporaryName = $"instant-quotation/temp/{sessionId:N}/{fileId:N}{normalized.Extension}";
        var now = _timeProvider.GetUtcNow();
        var reservation = await ExecuteDurableStateAsync(() => _repository.ReserveUploadAsync(new InstantQuoteUploadFile(
            fileId, sessionId, idempotencyHash, fingerprint, normalized.Metadata.FileName, normalized.Extension,
            normalized.Metadata.ContentType, headers.ExpectedSha256, null, null, null, _options.TemporaryBucket,
            temporaryName, null, null,
            InstantQuoteWorkflowState.Pending, now, now), now, _options.OperationLeaseTimeout, cancellationToken));

        if (reservation.Status == InstantQuoteReservationStatus.Conflict)
        {
            throw new InstantQuoteReplayConflictException("Idempotency key belongs to a different upload.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.InProgress)
        {
            throw new InstantQuoteUploadInProgressException("The upload is still in progress.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.LimitExceeded)
        {
            throw new InstantQuoteValidationException("The upload session has reached its file limit.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.Unknown)
        {
            return await ReconcileUnknownUploadAsync(reservation, cancellationToken);
        }
        if (reservation.Status == InstantQuoteReservationStatus.Recovered)
        {
            var existing = await ExecuteDependencyReadAsync(() => _storage.GetMetadataAsync(
                reservation.Record.TemporaryBucket,
                reservation.Record.TemporaryObjectName,
                cancellationToken));
            if (existing is not null || reservation.Record.State is InstantQuoteWorkflowState.Uploaded or InstantQuoteWorkflowState.Unknown)
            {
                return await ReconcileUnknownUploadAsync(reservation, cancellationToken);
            }
            return await StoreAndScanAsync(reservation, body, cancellationToken);
        }
        if (reservation.Status == InstantQuoteReservationStatus.Replay)
        {
            return ToUploadResponse(reservation.Record);
        }

        return await StoreAndScanAsync(reservation, body, cancellationToken);
    }

    private async Task<InstantQuoteFileResponse> ReconcileUnknownUploadAsync(
        InstantQuoteReservation<InstantQuoteUploadFile> reservation,
        CancellationToken cancellationToken)
    {
        var upload = reservation.Record;
        var metadata = await ExecuteDependencyReadAsync(() => _storage.GetMetadataAsync(
            upload.TemporaryBucket, upload.TemporaryObjectName, cancellationToken));
        if (metadata is null || metadata.SizeBytes <= 0)
        {
            throw new InstantQuoteAmbiguousOutcomeException("Temporary object could not be reconciled.");
        }

        var scan = await ExecuteDependencyReadAsync(() => ScanStoredGenerationAsync(metadata, cancellationToken));
        if (scan.SizeBytes != metadata.SizeBytes ||
            !FixedTimeHexEquals(scan.Sha256, upload.ExpectedSha256))
        {
            await CleanupReconciledGenerationAsync(upload, metadata);
            upload.State = InstantQuoteWorkflowState.Failed;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            return await PersistReconciledUploadAsync(upload, reservation.Version, cancellationToken);
        }
        if (scan.Result == InstantQuoteScanResult.Unsafe)
        {
            await CleanupReconciledGenerationAsync(upload, metadata);
            upload.State = InstantQuoteWorkflowState.Failed;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            return await PersistReconciledUploadAsync(upload, reservation.Version, cancellationToken);
        }
        if (scan.Result != InstantQuoteScanResult.Clean)
        {
            throw new InstantQuoteDependencyUnavailableException("The safety scanner is unavailable.");
        }

        try
        {
            InstantQuoteContentSignaturePolicy.Validate(upload.ValidatedExtension, scan.Prefix, metadata.SizeBytes);
        }
        catch (InstantQuoteUnsafeContentException)
        {
            await CleanupReconciledGenerationAsync(upload, metadata);
            upload.State = InstantQuoteWorkflowState.Failed;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            return await PersistReconciledUploadAsync(upload, reservation.Version, cancellationToken);
        }

        upload.ActualSha256 = scan.Sha256;
        upload.ActualSizeBytes = scan.SizeBytes;
        upload.GcsGeneration = metadata.Generation;
        upload.State = InstantQuoteWorkflowState.Clean;
        upload.ModifiedAt = _timeProvider.GetUtcNow();
        return await PersistReconciledUploadAsync(upload, reservation.Version, cancellationToken);
    }

    private async Task<InstantQuoteFileResponse> PersistReconciledUploadAsync(
        InstantQuoteUploadFile upload,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await _repository.SaveUploadAsync(upload, expectedVersion, cancellationToken);
            return ToUploadResponse(upload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InstantQuoteConcurrencyException)
        {
            var authoritative = await ExecuteDurableStateAsync(() => _repository.GetSessionFilesAsync(
                upload.SessionId, [upload.Id], cancellationToken));
            if (authoritative.Count != 1)
            {
                throw new InstantQuoteAmbiguousOutcomeException(
                    "The reconciled upload authority could not be reloaded.");
            }
            return ToUploadResponse(authoritative[0].Upload);
        }
        catch (InstantQuoteContractException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InstantQuoteAmbiguousOutcomeException(
                "The reconciled upload outcome could not be persisted.", exception);
        }
    }

    /// <inheritdoc />
    public async Task<FinalizeInstantQuoteFilesResponse> FinalizeAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        FinalizeInstantQuoteFilesRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.OperationTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await FinalizeCoreAsync(sessionId, owner, token, idempotencyKey, request, linked.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new InstantQuoteDependencyUnavailableException("The finalization operation timed out and requires retry.", exception);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        if (fileId == Guid.Empty)
        {
            throw new InstantQuoteValidationException("File identifier is invalid.");
        }
        var verified = await ExecuteDurableStateAsync(() => _repository.VerifySessionAsync(
            sessionId, SHA256.HashData(DecodeBase64Url(token)), owner.PrincipalId, owner.IsAuthenticated,
            _timeProvider.GetUtcNow(), cancellationToken));
        if (verified is null)
        {
            throw new InstantQuoteOwnershipException("The upload session could not be authorized.");
        }
        var files = await ExecuteDurableStateAsync(() =>
            _repository.GetSessionFilesAsync(sessionId, [fileId], cancellationToken));
        if (files.Count != 1)
        {
            throw new InstantQuoteOwnershipException("The file could not be authorized.");
        }
        var stored = files[0];
        var upload = stored.Upload;
        if (upload.State == InstantQuoteWorkflowState.Removed)
        {
            return;
        }
        if (upload.State is InstantQuoteWorkflowState.Pending or InstantQuoteWorkflowState.Uploaded)
        {
            throw new InstantQuoteUploadInProgressException("The upload is still in progress.");
        }
        if (upload.State == InstantQuoteWorkflowState.Finalized)
        {
            throw new InstantQuoteOwnershipException("Finalized files cannot be removed through the upload session.");
        }

        upload.State = InstantQuoteWorkflowState.Removed;
        upload.ModifiedAt = _timeProvider.GetUtcNow();
        var removalVersion = await ExecuteDurableStateAsync(() =>
            _repository.SaveUploadAsync(upload, stored.Version, cancellationToken));

        try
        {
            if (upload.GcsGeneration is not null)
            {
                await _storage.DeleteGenerationAsync(
                    upload.TemporaryBucket, upload.TemporaryObjectName, upload.GcsGeneration.Value, cancellationToken);
                upload.GcsGeneration = null;
                upload.TemporaryCleanupCompleted = true;
                upload.ModifiedAt = _timeProvider.GetUtcNow();
                await _repository.SaveUploadAsync(upload, removalVersion, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InstantQuoteDependencyUnavailableException(
                "The file is removed but temporary-object cleanup is pending.", exception);
        }
    }

    private async Task<FinalizeInstantQuoteFilesResponse> FinalizeCoreAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        FinalizeInstantQuoteFilesRequest request,
        CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        if (request.QuotationRequestId <= 0 || request.FileIds.Count == 0 ||
            request.FileIds.Count > InstantQuoteFileContract.MaximumFilesPerSession ||
            request.FileIds.Any(value => value == Guid.Empty) || request.FileIds.Distinct().Count() != request.FileIds.Count)
        {
            throw new InstantQuoteValidationException("Finalization selection is invalid.");
        }
        if (idempotencyKey.Length is < 16 or > 128 || idempotencyKey.Any(character => character is < '!' or > '~'))
        {
            throw new InstantQuoteValidationException("Idempotency key is invalid.");
        }

        var tokenHash = SHA256.HashData(DecodeBase64Url(token));
        var verified = await ExecuteDurableStateAsync(() => _repository.VerifySessionAsync(
            sessionId, tokenHash, owner.PrincipalId, owner.IsAuthenticated, _timeProvider.GetUtcNow(), cancellationToken));
        if (verified is null)
        {
            throw new InstantQuoteOwnershipException("The upload session could not be authorized.");
        }

        var selected = request.FileIds.Order().ToArray();
        var files = await ExecuteDurableStateAsync(() =>
            _repository.GetSessionFilesAsync(sessionId, selected, cancellationToken));
        if (files.Count != selected.Length ||
            files.Any(value => value.Upload.State is not (InstantQuoteWorkflowState.Clean or InstantQuoteWorkflowState.Finalized)))
        {
            throw new InstantQuoteOwnershipException("One or more selected files could not be authorized.");
        }
        if (files.Any(value => value.Upload.State == InstantQuoteWorkflowState.Finalized &&
            value.Upload.FinalizedQuotationRequestId != request.QuotationRequestId))
        {
            throw new InstantQuoteReplayConflictException("A selected file is already finalized for another quotation request.");
        }
        var quotationRequestId = request.QuotationRequestId.ToString(CultureInfo.InvariantCulture);
        var fingerprint = HashText($"{sessionId:N}\n{quotationRequestId}\n{string.Join(',', selected.Select(value => value.ToString("N")))}");
        var now = _timeProvider.GetUtcNow();
        var reservation = await ExecuteDurableStateAsync(() => _repository.ReserveFinalizationAsync(new InstantQuoteFinalization(
            Guid.NewGuid(), sessionId, SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)), fingerprint,
            request.QuotationRequestId, selected, InstantQuoteWorkflowState.Pending, now, now),
            now, _options.OperationLeaseTimeout, cancellationToken));
        if (reservation.Status == InstantQuoteReservationStatus.Conflict)
        {
            throw new InstantQuoteReplayConflictException("Idempotency key belongs to another finalization.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.InProgress)
        {
            throw new InstantQuoteUploadInProgressException("Finalization is still in progress.");
        }
        var results = new List<FinalizedInstantQuoteFileResponse>(files.Count);
        try
        {
            foreach (var stored in files.OrderBy(value => value.Upload.Id))
            {
                var upload = stored.Upload;
                var destination = $"instant-quotation/{quotationRequestId}/{upload.Id:N}{upload.ValidatedExtension}";
                InstantQuoteObjectMetadata final;
                if (upload.State == InstantQuoteWorkflowState.Finalized)
                {
                    if (string.IsNullOrWhiteSpace(upload.FinalBucket))
                    {
                        throw new InstantQuoteAmbiguousOutcomeException("Finalized object bucket is unavailable.");
                    }
                    final = await _storage.GetMetadataAsync(
                        upload.FinalBucket, upload.FinalObjectName ?? destination, cancellationToken)
                        ?? throw new InstantQuoteAmbiguousOutcomeException("Finalized object metadata is unavailable.");
                }
                else
                {
                    if (upload.GcsGeneration is null)
                    {
                        throw new InstantQuoteAmbiguousOutcomeException("Clean upload generation is unavailable.");
                    }
                    var existing = await _storage.GetMetadataAsync(_options.FinalBucket, destination, cancellationToken);
                    var promoted = false;
                    if (existing is not null)
                    {
                        if (existing.SizeBytes != upload.ActualSizeBytes ||
                            !FixedTimeHexEquals(existing.Sha256, upload.ActualSha256!))
                        {
                            throw new InstantQuoteAmbiguousOutcomeException("Final object does not match the upload.");
                        }
                        final = existing;
                    }
                    else
                    {
                        final = await _storage.PromoteGenerationAsync(
                            upload.TemporaryBucket, upload.TemporaryObjectName, upload.GcsGeneration.Value,
                            _options.FinalBucket, destination, cancellationToken);
                        promoted = true;
                    }
                    upload.FinalBucket = final.Bucket;
                    upload.FinalObjectName = final.ObjectName;
                    upload.FinalizedQuotationRequestId = request.QuotationRequestId;
                    upload.State = InstantQuoteWorkflowState.Finalized;
                    upload.ModifiedAt = _timeProvider.GetUtcNow();
                    try
                    {
                        await _repository.SaveUploadAsync(upload, stored.Version, cancellationToken);
                    }
                    catch (InstantQuoteConcurrencyException)
                    {
                        var reloaded = await _repository.GetSessionFilesAsync(sessionId, [upload.Id], cancellationToken);
                        var winner = reloaded.Count == 1 ? reloaded[0].Upload : null;
                        if (winner?.State == InstantQuoteWorkflowState.Finalized &&
                            winner.FinalizedQuotationRequestId == request.QuotationRequestId &&
                            !string.IsNullOrWhiteSpace(winner.FinalBucket) &&
                            !string.IsNullOrWhiteSpace(winner.FinalObjectName))
                        {
                            upload = winner;
                            final = await _storage.GetMetadataAsync(
                                winner.FinalBucket, winner.FinalObjectName, cancellationToken)
                                ?? throw new InstantQuoteAmbiguousOutcomeException(
                                    "The concurrently finalized object metadata is unavailable.");
                        }
                        else
                        {
                            if (promoted)
                            {
                                await DeleteLoserDestinationAsync(final);
                            }
                            throw new InstantQuoteReplayConflictException(
                                "The selected file was concurrently finalized for another quotation request.");
                        }
                    }
                }
                results.Add(new FinalizedInstantQuoteFileResponse(
                    upload.Id, final.Bucket, final.ObjectName, upload.OriginalFileName, upload.ValidatedContentType,
                    upload.ActualSizeBytes!.Value, upload.ActualSha256!, "finalized"));
            }

            reservation.Record.State = InstantQuoteWorkflowState.Finalized;
            reservation.Record.ModifiedAt = _timeProvider.GetUtcNow();
            await _repository.SaveFinalizationAsync(reservation.Record, reservation.Version, cancellationToken);
            return new FinalizeInstantQuoteFilesResponse(reservation.Record.QuotationRequestId, results);
        }
        catch (OperationCanceledException)
        {
            await PersistUnknownFinalizationAsync(reservation);
            throw;
        }
        catch (InstantQuoteAmbiguousOutcomeException)
        {
            await PersistUnknownFinalizationAsync(reservation);
            throw;
        }
        catch (InstantQuoteContractException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistUnknownFinalizationAsync(reservation);
            throw new InstantQuoteAmbiguousOutcomeException("Finalization outcome requires reconciliation.", exception);
        }
    }

    private async Task PersistUnknownFinalizationAsync(
        InstantQuoteReservation<InstantQuoteFinalization> reservation)
    {
        reservation.Record.State = InstantQuoteWorkflowState.Unknown;
        reservation.Record.ModifiedAt = _timeProvider.GetUtcNow();
        try
        {
            using var save = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
            await _repository.SaveFinalizationAsync(reservation.Record, reservation.Version, save.Token);
        }
        catch (Exception)
        {
        }
    }

    private async Task<InstantQuoteFileResponse> StoreAndScanAsync(
        InstantQuoteReservation<InstantQuoteUploadFile> reservation,
        Stream body,
        CancellationToken cancellationToken)
    {
        var upload = reservation.Record;
        InstantQuoteObjectMetadata? stored = null;
        var version = reservation.Version;
        try
        {
            await using var bounded = new BoundedHashingReadStream(body);
            await using var captured = new PrefixCapturingReadStream(bounded, 4096);
            stored = await _storage.UploadTemporaryAsync(
                upload.TemporaryBucket, upload.TemporaryObjectName, captured, upload.ExpectedSha256, cancellationToken);
            if (!bounded.IsComplete || bounded.BytesRead == 0 || bounded.BytesRead != stored.SizeBytes ||
                !FixedTimeHexEquals(bounded.Sha256, upload.ExpectedSha256) ||
                !FixedTimeHexEquals(bounded.Sha256, stored.Sha256))
            {
                throw new InstantQuoteUnsafeContentException("Uploaded bytes do not match the declared digest or size.");
            }

            InstantQuoteContentSignaturePolicy.Validate(upload.ValidatedExtension, captured.Prefix, bounded.BytesRead);
            upload.ActualSha256 = bounded.Sha256;
            upload.ActualSizeBytes = bounded.BytesRead;
            upload.GcsGeneration = stored.Generation;
            upload.State = InstantQuoteWorkflowState.Uploaded;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            version = await _repository.SaveUploadAsync(upload, version, cancellationToken);
            var scan = await ScanStoredGenerationAsync(stored, cancellationToken);
            if (scan.Result == InstantQuoteScanResult.Unsafe)
            {
                throw new InstantQuoteUnsafeContentException("Uploaded content is unsafe.");
            }
            if (scan.Result != InstantQuoteScanResult.Clean)
            {
                throw new InstantQuoteDependencyUnavailableException("The safety scanner is unavailable.");
            }

            upload.State = InstantQuoteWorkflowState.Clean;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await _repository.SaveUploadAsync(upload, version, cancellationToken);
            return ToUploadResponse(upload);
        }
        catch (OperationCanceledException)
        {
            upload.State = InstantQuoteWorkflowState.Unknown;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await SaveUnknownIgnoringCancellationAsync(upload, version);
            throw;
        }
        catch (InstantQuotePayloadTooLargeException)
        {
            await CleanupDiscoveredTemporaryGenerationAsync(upload);
            upload.State = InstantQuoteWorkflowState.PayloadTooLarge;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await SaveTerminalIgnoringCancellationAsync(upload, version);
            throw;
        }
        catch (InstantQuoteValidationException)
        {
            await CleanupDiscoveredTemporaryGenerationAsync(upload);
            upload.State = InstantQuoteWorkflowState.InvalidRequest;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await SaveTerminalIgnoringCancellationAsync(upload, version);
            throw;
        }
        catch (InstantQuoteUnsafeContentException)
        {
            if (stored is not null)
            {
                await CleanupReconciledGenerationAsync(upload, stored);
            }
            upload.State = InstantQuoteWorkflowState.Failed;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await SaveTerminalIgnoringCancellationAsync(upload, version);
            throw;
        }
        catch (InstantQuoteDependencyUnavailableException)
        {
            upload.State = InstantQuoteWorkflowState.Unknown;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await SaveTerminalIgnoringCancellationAsync(upload, version);
            throw;
        }
        catch (Exception exception)
        {
            upload.State = InstantQuoteWorkflowState.Unknown;
            upload.ModifiedAt = _timeProvider.GetUtcNow();
            await SaveUnknownIgnoringCancellationAsync(upload, version);
            throw new InstantQuoteAmbiguousOutcomeException("Upload outcome requires reconciliation.", exception);
        }
    }

    private async Task<InstantQuoteScanOutcome> ScanStoredGenerationAsync(
        InstantQuoteObjectMetadata stored,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var reader = pipe.Reader.AsStream(leaveOpen: true);
        await using var writer = pipe.Writer.AsStream(leaveOpen: true);
        await using var hashing = new BoundedHashingReadStream(reader);
        await using var captured = new PrefixCapturingReadStream(hashing, 4096);
        var producerCompleted = false;
        var download = DownloadAsync();
        var scan = _scanner.ScanAsync(captured, linkedCancellation.Token);

        try
        {
            var first = await Task.WhenAny(download, scan);
            if (ReferenceEquals(first, scan) && !Volatile.Read(ref producerCompleted))
            {
                linkedCancellation.Cancel();
                await ObserveProducerCancellationAsync(download);
                await ObserveScannerAsync(scan, cancellationToken);
                throw new InstantQuoteDependencyUnavailableException(
                    "The safety scanner stopped before the complete object was delivered.");
            }

            await download;
            var result = await ObserveScannerAsync(scan, cancellationToken);
            if (!Volatile.Read(ref producerCompleted))
            {
                throw new InstantQuoteDependencyUnavailableException(
                    "The complete object could not be delivered to the safety scanner.");
            }
            if (!hashing.IsComplete)
            {
                throw new InstantQuoteDependencyUnavailableException(
                    "The complete object was not consumed by the safety scanner.");
            }
            return new InstantQuoteScanOutcome(result, captured.Prefix.ToArray(), hashing.Sha256, hashing.BytesRead);
        }
        finally
        {
            linkedCancellation.Cancel();
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }

        async Task DownloadAsync()
        {
            Exception? error = null;
            try
            {
                await _storage.DownloadGenerationAsync(stored.Bucket, stored.ObjectName, stored.Generation,
                    writer, linkedCancellation.Token);
                Volatile.Write(ref producerCompleted, true);
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

        static async Task ObserveProducerCancellationAsync(Task producer)
        {
            try
            {
                await producer;
            }
            catch (OperationCanceledException)
            {
            }
        }

        static async Task<InstantQuoteScanResult> ObserveScannerAsync(
            Task<InstantQuoteScanResult> scannerTask,
            CancellationToken callerCancellation)
        {
            try
            {
                return await scannerTask;
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InstantQuoteDependencyUnavailableException("The safety scanner is unavailable.", exception);
            }
        }
    }

    private async Task<bool> CleanupAsync(InstantQuoteObjectMetadata stored)
    {
        using var cleanup = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
        try
        {
            await _storage.DeleteGenerationAsync(stored.Bucket, stored.ObjectName, stored.Generation, cleanup.Token);
            return true;
        }
        catch (Exception)
        {
            // Cleanup is bounded and best effort; the failed state remains non-authoritative.
            return false;
        }
    }

    private async Task CleanupReconciledGenerationAsync(
        InstantQuoteUploadFile upload,
        InstantQuoteObjectMetadata metadata)
    {
        upload.GcsGeneration = metadata.Generation;
        upload.TemporaryCleanupCompleted = await CleanupAsync(metadata);
        if (upload.TemporaryCleanupCompleted)
        {
            upload.GcsGeneration = null;
        }
    }

    private async Task CleanupDiscoveredTemporaryGenerationAsync(InstantQuoteUploadFile upload)
    {
        using var cleanup = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
        try
        {
            var metadata = await _storage.GetMetadataAsync(
                upload.TemporaryBucket,
                upload.TemporaryObjectName,
                cleanup.Token);
            if (metadata is not null)
            {
                upload.GcsGeneration = metadata.Generation;
                upload.TemporaryCleanupCompleted = await CleanupAsync(metadata);
                if (upload.TemporaryCleanupCompleted)
                {
                    upload.GcsGeneration = null;
                }
            }
            else
            {
                upload.TemporaryCleanupCompleted = true;
            }
        }
        catch (Exception)
        {
            // A deterministic-name retry or lifecycle sweep can reconcile the remaining exact generation.
        }
    }

    private async Task DeleteLoserDestinationAsync(InstantQuoteObjectMetadata stored)
    {
        using var cleanup = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
        try
        {
            await _storage.DeleteGenerationAsync(
                stored.Bucket, stored.ObjectName, stored.Generation, cleanup.Token);
        }
        catch (Exception exception)
        {
            throw new InstantQuoteAmbiguousOutcomeException(
                "A concurrent finalization left a destination requiring reconciliation.", exception);
        }
    }

    private Task SaveUnknownIgnoringCancellationAsync(InstantQuoteUploadFile upload, uint version) =>
        SaveTerminalIgnoringCancellationAsync(upload, version);

    private async Task SaveTerminalIgnoringCancellationAsync(InstantQuoteUploadFile upload, uint version)
    {
        using var save = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
        try
        {
            await _repository.SaveUploadAsync(upload, version, save.Token);
        }
        catch (Exception)
        {
            // The caller receives outcome_unknown even if durable reconciliation state cannot be written.
        }
    }

    private static InstantQuoteFileResponse ToUploadResponse(InstantQuoteUploadFile upload)
    {
        if (upload.State is not (InstantQuoteWorkflowState.Clean or InstantQuoteWorkflowState.Finalized) ||
            upload.ActualSha256 is null || upload.ActualSizeBytes is null)
        {
            if (upload.State == InstantQuoteWorkflowState.Failed)
            {
                throw new InstantQuoteUnsafeContentException("The recorded upload was rejected as unsafe.");
            }
            if (upload.State == InstantQuoteWorkflowState.PayloadTooLarge)
            {
                throw new InstantQuotePayloadTooLargeException("Uploaded file exceeds the maximum size.");
            }
            if (upload.State == InstantQuoteWorkflowState.InvalidRequest)
            {
                throw new InstantQuoteValidationException("The recorded upload request is invalid.");
            }
            throw new InstantQuoteAmbiguousOutcomeException("The recorded upload is not replayable.");
        }
        return new(upload.Id, upload.OriginalFileName, upload.ValidatedContentType, upload.ActualSizeBytes.Value,
            upload.ActualSha256, upload.State == InstantQuoteWorkflowState.Clean ? "clean" : "finalized");
    }

    private static async Task<T> ExecuteDurableStateAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InstantQuoteContractException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InstantQuoteDependencyUnavailableException(
                "Durable instant quotation state is unavailable.", exception);
        }
    }

    private static async Task<T> ExecuteDependencyReadAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InstantQuoteContractException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InstantQuoteDependencyUnavailableException(
                "A required instant quotation dependency is unavailable.", exception);
        }
    }

    private void EnsureWritesEnabled()
    {
        if (!_options.Enabled || !_options.WritesEnabled)
        {
            throw new InstantQuoteDependencyUnavailableException("Instant quotation file writes are disabled.");
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new InstantQuoteOwnershipException("The upload session could not be authorized.");
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class PrefixCapturingReadStream(Stream source, int maximumPrefixBytes) : Stream
    {
        private readonly MemoryStream _prefix = new(maximumPrefixBytes);
        public ReadOnlySpan<byte> Prefix => _prefix.GetBuffer().AsSpan(0, checked((int)_prefix.Length));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            var remaining = maximumPrefixBytes - checked((int)_prefix.Length);
            if (remaining > 0 && read > 0)
            {
                _prefix.Write(buffer.Span[..Math.Min(read, remaining)]);
            }
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await source.DisposeAsync(); await _prefix.DisposeAsync(); GC.SuppressFinalize(this); }
    }

    private sealed record InstantQuoteScanOutcome(
        InstantQuoteScanResult Result,
        byte[] Prefix,
        string Sha256,
        long SizeBytes);
}
