using System.Security.Cryptography;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.Options;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Reuses private scanner, generation move and durable journal boundaries without signing.</summary>
public sealed class CustomerDocumentStorageAdapter(IObjectStorage objects, IFileSafetyScanner scanner, IStorageMoveJournal journal, IProtectedDocumentGenerationReader reader, IOptions<CustomerDocumentOptions> options, IQuarantineUploadIntent? quarantineIntents = null) : IProtectedDocumentStorage
{
    /// <inheritdoc />
    public async Task<DocumentStoredContent> StoreAsync(DocumentUploadReservation reservation, ValidatedDocumentContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await StoreCoreAsync(reservation, content, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await PreserveUnknownAsync(reservation.OperationId); throw; }
        catch (DocumentContentException) { await PreserveUnknownAsync(reservation.OperationId); throw; }
        catch (DocumentAuthorityDeniedException) { await PreserveUnknownAsync(reservation.OperationId); throw; }
        catch (Exception) { await PreserveUnknownAsync(reservation.OperationId); throw new DocumentAuthorityUnavailableException(); }
    }
    private async Task<DocumentStoredContent> StoreCoreAsync(DocumentUploadReservation reservation, ValidatedDocumentContent content, CancellationToken token)
    {
        var bucket = options.Value.PrivateBucket;
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(bucket) || quarantineIntents is null) throw new DocumentAuthorityUnavailableException();
        var prefix = $"{CustomerDocumentOptions.ReservedPrefix}{reservation.CustomerId}/{reservation.DocumentId:N}/{reservation.VersionId:N}";
        var quarantine = $"{prefix}/quarantine/{reservation.OperationId:N}";
        var destination = $"{prefix}/original";
        if (await journal.FindAsync(reservation.OperationId, token) is not null) throw new DocumentAuthorityUnavailableException();
        await quarantineIntents.PrepareAsync(reservation.OperationId, reservation.OperationId, bucket, quarantine, content.ContentType, content.Bytes.Length, token);
        using var captured = new MemoryStream(content.Bytes.ToArray(), false);
        var sourceGeneration = await objects.UploadGenerationAsync(bucket, quarantine, content.ContentType, captured, token);
        if (sourceGeneration <= 0) throw new DocumentAuthorityUnavailableException();
        await quarantineIntents.AcknowledgeAsync(reservation.OperationId, sourceGeneration, token);
        // Certification binds the complete scanner snapshot to the actual acknowledged private generation.
        var uploaded = await reader.ReadAsync(bucket, quarantine, sourceGeneration, CustomerDocumentOptions.MaximumBytes, token);
        if (uploaded.Length != content.Bytes.Length || Convert.ToHexStringLower(SHA256.HashData(uploaded.Span)) != content.Sha256) throw new DocumentAuthorityUnavailableException();
        var result = await scanner.ScanAsync(new CapturedUpload(content), token);
        if (result.Verdict == FileSafetyVerdict.Infected) throw new DocumentContentException(422);
        if (result.Verdict != FileSafetyVerdict.Clean) throw new DocumentAuthorityUnavailableException();
        var live = await objects.GetEvidenceAsync(bucket, quarantine, token);
        if (live is null || live.Generation != sourceGeneration || live.Size != content.Bytes.Length) throw new DocumentAuthorityUnavailableException();
        if (!await objects.MoveJournaledAsync(reservation.OperationId, sourceGeneration, true, bucket, quarantine, bucket, destination, token)) throw new DocumentAuthorityUnavailableException();
        var proof = await journal.FindAsync(reservation.OperationId, token);
        if (proof is null || !proof.ScanClean || proof.SourceBucket != bucket || proof.SourceObjectName != quarantine || proof.SourceGeneration != sourceGeneration || proof.DestinationBucket != bucket || proof.DestinationObjectName != destination || proof.DestinationGeneration is not > 0 || proof.State != "SourceDeleted") throw new DocumentAuthorityUnavailableException();
        return new(bucket, destination, proof.DestinationGeneration.Value, sourceGeneration, reservation.OperationId, content.Bytes.Length, content.Sha256, content.ContentType, content.FileName);
    }
    /// <inheritdoc />
    public async Task ReconcileCommittedAsync(DocumentStoredContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!options.Value.Enabled) throw new DocumentAuthorityUnavailableException();
            var proof = await journal.FindAsync(content.ScanOperationId, token);
            if (proof?.State == "MetadataCommitted") return;
            if (proof?.State != "SourceDeleted") throw new DocumentAuthorityUnavailableException();
            // The caller supplies a server-read sealed version after current write/association authority.
            // This verifies all immutable coordinates and exact bytes before acknowledging metadata only.
            await ReadCoreAsync(content, token, allowSourceDeleted: true);
            await journal.MetadataCommittedAsync(content.ScanOperationId, token);
            await ReadCoreAsync(content, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (DocumentAuthorityDeniedException) { throw; }
        catch (Exception) { throw new DocumentAuthorityUnavailableException(); }
    }
    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> ReadAsync(DocumentStoredContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await ReadCoreAsync(content, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (DocumentAuthorityDeniedException) { throw; }
        catch (Exception) { throw new DocumentAuthorityUnavailableException(); }
    }
    private async Task<ReadOnlyMemory<byte>> ReadCoreAsync(DocumentStoredContent content, CancellationToken token, bool allowSourceDeleted = false)
    {
        if (!options.Value.Enabled || content.Bucket != options.Value.PrivateBucket || !content.ObjectName.StartsWith(CustomerDocumentOptions.ReservedPrefix, StringComparison.Ordinal) || !content.ObjectName.EndsWith("/original", StringComparison.Ordinal) || content.Generation <= 0 || content.SourceGeneration <= 0 || content.Size is <= 0 or > CustomerDocumentOptions.MaximumBytes) throw new DocumentAuthorityUnavailableException();
        var proof = await journal.FindAsync(content.ScanOperationId, token);
        if (proof is null || !proof.ScanClean || !(proof.State == "MetadataCommitted" || allowSourceDeleted && proof.State == "SourceDeleted") || proof.SourceBucket != content.Bucket || proof.SourceObjectName != $"{content.ObjectName[..^9]}/quarantine/{content.ScanOperationId:N}" || proof.SourceGeneration != content.SourceGeneration || proof.DestinationGeneration != content.Generation || proof.DestinationBucket != content.Bucket || proof.DestinationObjectName != content.ObjectName) throw new DocumentAuthorityUnavailableException();
        var live = await objects.GetEvidenceAsync(content.Bucket, content.ObjectName, token);
        if (live is null || live.Generation != content.Generation || live.Size != content.Size) throw new DocumentAuthorityUnavailableException();
        var bytes = await reader.ReadAsync(content.Bucket, content.ObjectName, content.Generation, CustomerDocumentOptions.MaximumBytes, token);
        if (bytes.Length != content.Size || Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) != content.Sha256) throw new DocumentAuthorityUnavailableException();
        return bytes.ToArray();
    }
    private async Task PreserveUnknownAsync(Guid operationId)
    {
        if (quarantineIntents is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await quarantineIntents.UnknownAsync(operationId, timeout.Token).WaitAsync(timeout.Token); }
        catch (Exception) { /* Retain the durable pending coordinates and any acknowledged generation. */ }
    }
    private sealed class CapturedUpload(ValidatedDocumentContent content) : IUploadFile
    {
        public string FileName => content.FileName;
        public string ContentType => content.ContentType;
        public long Length => content.Bytes.Length;
        public Stream OpenReadStream() => new MemoryStream(content.Bytes.ToArray(), false);
    }
}
