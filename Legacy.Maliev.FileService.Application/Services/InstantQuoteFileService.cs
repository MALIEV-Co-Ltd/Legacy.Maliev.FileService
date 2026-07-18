using System.Security.Cryptography;
using System.Text;
using System.IO.Pipelines;
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
        var token = RandomNumberGenerator.GetBytes(32);
        var now = _timeProvider.GetUtcNow();
        var session = new InstantQuoteUploadSession(
            Guid.NewGuid(),
            owner.PrincipalId,
            owner.IsAuthenticated,
            SHA256.HashData(token),
            now.Add(_options.SessionLifetime),
            now);
        await _repository.CreateSessionAsync(session, cancellationToken);
        return new CreateInstantQuoteSessionResponse(
            session.Id,
            Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            session.ExpiresAt,
            InstantQuoteFileContract.MaximumUploadBytes,
            InstantQuoteFileContract.SupportedExtensions);
    }

    /// <inheritdoc />
    public Task<InstantQuoteFileResponse> UploadAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        string expectedSha256,
        Stream body,
        InstantQuoteUploadMetadata metadata,
        CancellationToken cancellationToken) => UploadCoreAsync(
            sessionId, owner, token, idempotencyKey, expectedSha256, body, metadata, cancellationToken);

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
        var headers = InstantQuoteFilePolicy.NormalizeHeaders(token, idempotencyKey, expectedSha256);
        var normalized = InstantQuoteFilePolicy.NormalizeFileMetadata(metadata.FileName, metadata.ContentType);
        var tokenHash = SHA256.HashData(DecodeBase64Url(headers.Token));
        var session = await _repository.VerifySessionAsync(
            sessionId, tokenHash, owner.PrincipalId, owner.IsAuthenticated, _timeProvider.GetUtcNow(), cancellationToken);
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
        var reservation = await _repository.ReserveUploadAsync(new InstantQuoteUploadFile(
            fileId, sessionId, idempotencyHash, fingerprint, normalized.Metadata.FileName, normalized.Extension,
            normalized.Metadata.ContentType, headers.ExpectedSha256, null, null, null, temporaryName, null,
            InstantQuoteWorkflowState.Pending, now, now), cancellationToken);

        if (reservation.Status == InstantQuoteReservationStatus.Conflict)
        {
            throw new InstantQuoteReplayConflictException("Idempotency key belongs to a different upload.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.InProgress)
        {
            throw new InstantQuoteUploadInProgressException("The upload is still in progress.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.Unknown)
        {
            return await ReconcileUnknownUploadAsync(reservation, cancellationToken);
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
        var metadata = await _storage.GetMetadataAsync(string.Empty, upload.TemporaryObjectName, cancellationToken);
        if (metadata is null || metadata.SizeBytes <= 0 || !FixedTimeHexEquals(metadata.Sha256, upload.ExpectedSha256))
        {
            throw new InstantQuoteAmbiguousOutcomeException("Temporary object could not be reconciled.");
        }

        var scan = await ScanStoredGenerationAsync(metadata, cancellationToken);
        if (scan == InstantQuoteScanResult.Unsafe)
        {
            await CleanupAsync(metadata);
            throw new InstantQuoteUnsafeContentException("Uploaded content is unsafe.");
        }
        if (scan != InstantQuoteScanResult.Clean)
        {
            throw new InstantQuoteDependencyUnavailableException("The safety scanner is unavailable.");
        }

        upload.ActualSha256 = metadata.Sha256;
        upload.ActualSizeBytes = metadata.SizeBytes;
        upload.GcsGeneration = metadata.Generation;
        upload.State = InstantQuoteWorkflowState.Clean;
        upload.ModifiedAt = _timeProvider.GetUtcNow();
        await _repository.SaveUploadAsync(upload, reservation.Version, cancellationToken);
        return ToUploadResponse(upload);
    }

    /// <inheritdoc />
    public Task<FinalizeInstantQuoteFilesResponse> FinalizeAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        FinalizeInstantQuoteFilesRequest request,
        CancellationToken cancellationToken) => FinalizeCoreAsync(
            sessionId, owner, token, idempotencyKey, request, cancellationToken);

    private async Task<FinalizeInstantQuoteFilesResponse> FinalizeCoreAsync(
        Guid sessionId,
        InstantQuoteOwner owner,
        string token,
        string idempotencyKey,
        FinalizeInstantQuoteFilesRequest request,
        CancellationToken cancellationToken)
    {
        if (request.QuotationRequestId == Guid.Empty || request.FileIds.Count == 0 ||
            request.FileIds.Any(value => value == Guid.Empty) || request.FileIds.Distinct().Count() != request.FileIds.Count)
        {
            throw new InstantQuoteValidationException("Finalization selection is invalid.");
        }
        if (idempotencyKey.Length is < 16 or > 128 || idempotencyKey.Any(character => character is < '!' or > '~'))
        {
            throw new InstantQuoteValidationException("Idempotency key is invalid.");
        }

        var tokenHash = SHA256.HashData(DecodeBase64Url(token));
        var verified = await _repository.VerifySessionAsync(
            sessionId, tokenHash, owner.PrincipalId, owner.IsAuthenticated, _timeProvider.GetUtcNow(), cancellationToken);
        if (verified is null)
        {
            throw new InstantQuoteOwnershipException("The upload session could not be authorized.");
        }

        var selected = request.FileIds.Order().ToArray();
        var fingerprint = HashText($"{sessionId:N}\n{request.QuotationRequestId:N}\n{string.Join(',', selected.Select(value => value.ToString("N")))}");
        var now = _timeProvider.GetUtcNow();
        var reservation = await _repository.ReserveFinalizationAsync(new InstantQuoteFinalization(
            Guid.NewGuid(), sessionId, SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)), fingerprint,
            request.QuotationRequestId, selected, InstantQuoteWorkflowState.Pending, now, now), cancellationToken);
        if (reservation.Status == InstantQuoteReservationStatus.Conflict)
        {
            throw new InstantQuoteReplayConflictException("Idempotency key belongs to another finalization.");
        }
        if (reservation.Status == InstantQuoteReservationStatus.InProgress)
        {
            throw new InstantQuoteUploadInProgressException("Finalization is still in progress.");
        }
        var files = await _repository.GetSessionFilesAsync(sessionId, reservation.Record.SelectedFileIds, cancellationToken);
        if (files.Count != reservation.Record.SelectedFileIds.Length ||
            files.Any(value => value.Upload.State is not (InstantQuoteWorkflowState.Clean or InstantQuoteWorkflowState.Finalized)))
        {
            throw new InstantQuoteOwnershipException("One or more selected files could not be authorized.");
        }

        var results = new List<FinalizedInstantQuoteFileResponse>(files.Count);
        try
        {
            foreach (var stored in files.OrderBy(value => value.Upload.Id))
            {
                var upload = stored.Upload;
                var destination = $"instant-quotation/{request.QuotationRequestId:N}/{upload.Id:N}{upload.ValidatedExtension}";
                InstantQuoteObjectMetadata final;
                if (upload.State == InstantQuoteWorkflowState.Finalized)
                {
                    final = await _storage.GetMetadataAsync(string.Empty, upload.FinalObjectName ?? destination, cancellationToken)
                        ?? throw new InstantQuoteAmbiguousOutcomeException("Finalized object metadata is unavailable.");
                }
                else
                {
                    if (upload.GcsGeneration is null)
                    {
                        throw new InstantQuoteAmbiguousOutcomeException("Clean upload generation is unavailable.");
                    }
                    final = await _storage.PromoteGenerationAsync(
                        string.Empty, upload.TemporaryObjectName, upload.GcsGeneration.Value, destination, cancellationToken);
                    upload.FinalObjectName = final.ObjectName;
                    upload.State = InstantQuoteWorkflowState.Finalized;
                    upload.ModifiedAt = _timeProvider.GetUtcNow();
                    await _repository.SaveUploadAsync(upload, stored.Version, cancellationToken);
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
        catch (InstantQuoteContractException)
        {
            throw;
        }
        catch (Exception exception)
        {
            reservation.Record.State = InstantQuoteWorkflowState.Unknown;
            reservation.Record.ModifiedAt = _timeProvider.GetUtcNow();
            try
            {
                await _repository.SaveFinalizationAsync(reservation.Record, reservation.Version, CancellationToken.None);
            }
            catch (Exception)
            {
            }
            throw new InstantQuoteAmbiguousOutcomeException("Finalization outcome requires reconciliation.", exception);
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
                upload.TemporaryObjectName, captured, upload.ExpectedSha256, cancellationToken);
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
            if (scan == InstantQuoteScanResult.Unsafe)
            {
                throw new InstantQuoteUnsafeContentException("Uploaded content is unsafe.");
            }
            if (scan != InstantQuoteScanResult.Clean)
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
            if (stored is not null)
            {
                upload.State = InstantQuoteWorkflowState.Unknown;
                upload.ModifiedAt = _timeProvider.GetUtcNow();
                await SaveUnknownIgnoringCancellationAsync(upload, version);
            }
            throw;
        }
        catch (InstantQuoteContractException)
        {
            if (stored is not null)
            {
                await CleanupAsync(stored);
            }
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

    private async Task<InstantQuoteScanResult> ScanStoredGenerationAsync(
        InstantQuoteObjectMetadata stored,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        var download = DownloadAsync();
        var scan = _scanner.ScanAsync(pipe.Reader.AsStream(), cancellationToken);
        await Task.WhenAll(download, scan);
        return await scan;

        async Task DownloadAsync()
        {
            Exception? error = null;
            try
            {
                await _storage.DownloadGenerationAsync(stored.Bucket, stored.ObjectName, stored.Generation,
                    pipe.Writer.AsStream(), cancellationToken);
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

    private async Task CleanupAsync(InstantQuoteObjectMetadata stored)
    {
        using var cleanup = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
        try
        {
            await _storage.DeleteGenerationAsync(stored.Bucket, stored.ObjectName, stored.Generation, cleanup.Token);
        }
        catch (Exception)
        {
            // Cleanup is bounded and best effort; the failed state remains non-authoritative.
        }
    }

    private async Task SaveUnknownIgnoringCancellationAsync(InstantQuoteUploadFile upload, uint version)
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
            throw new InstantQuoteAmbiguousOutcomeException("The recorded upload is not replayable.");
        }
        return new(upload.Id, upload.OriginalFileName, upload.ValidatedContentType, upload.ActualSizeBytes.Value,
            upload.ActualSha256, upload.State == InstantQuoteWorkflowState.Clean ? "clean" : "finalized");
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
}
