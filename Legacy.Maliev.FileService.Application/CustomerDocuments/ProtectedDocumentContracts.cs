using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Requests a new immutable document or replacement version.</summary>
public sealed record DocumentUploadRequest(Guid? DocumentId, DocumentKind Kind, string Title, DocumentVisibility Visibility, IReadOnlyList<DocumentAssociation> Associations, long? ExpectedRevision = null);
/// <summary>Returns finalized metadata without storage coordinates.</summary>
public sealed record DocumentVersionReceipt(Guid DocumentId, Guid VersionId, int CustomerId, int VersionNumber, string ContentSha256, long Revision);
/// <summary>Returns fully buffered authenticated attachment content.</summary>
public sealed record DocumentDownload(ReadOnlyMemory<byte> Bytes, string ContentType, string FileName);
/// <summary>Captures an exclusive durable operation reservation.</summary>
public sealed record DocumentUploadReservation(Guid OperationId, Guid DocumentId, Guid VersionId, int CustomerId, int VersionNumber, string Fingerprint, DocumentVersionReceipt? Replay = null);
/// <summary>Captures private exact-generation clean promotion evidence.</summary>
public sealed record DocumentStoredContent(string Bucket, string ObjectName, long Generation, long SourceGeneration, Guid ScanOperationId, long Size, string Sha256, string ContentType, string FileName);
/// <summary>Owns durable idempotency, serialized versions and atomic finalized evidence.</summary>
public interface IProtectedDocumentStore
{
    /// <summary>Reserves tenant/actor/key/fingerprint atomically before any storage mutation.</summary>
    Task<DocumentUploadReservation> ReserveAsync(int customerId, string subject, string key, string fingerprint, DocumentUploadRequest request, CancellationToken token);
    /// <summary>Finalizes exact bytes, sealed canonical associations and audit atomically.</summary>
    Task<DocumentVersionReceipt> FinalizeAsync(DocumentActor actor, DocumentUploadReservation reservation, DocumentUploadRequest request, DocumentStoredContent content, string subject, CancellationToken token);
    /// <summary>Retains uncertain outcomes without destructive compensation.</summary>
    Task MarkUnknownAsync(Guid operationId, CancellationToken token);
    /// <summary>Reads exact private evidence after visibility and customer checks.</summary>
    Task<DocumentStoredContent?> FindAsync(int customerId, Guid documentId, Guid versionId, DocumentActorKind actorKind, CancellationToken token);
    /// <summary>Appends successful attachment authorization/transfer audit.</summary>
    Task AuditReadAsync(int customerId, Guid documentId, Guid versionId, string subject, long authorizedRevision, CancellationToken token);
    /// <summary>Archives with expected revision, reason and current legal-hold check; retains originals.</summary>
    Task ArchiveAsync(int customerId, Guid documentId, long expectedRevision, string reason, string subject, DocumentActorKind actorKind, CancellationToken token);
}
/// <summary>Scans and promotes immutable private content and validates buffered downloads.</summary>
public interface IProtectedDocumentStorage
{
    /// <summary>Stores exact detached bytes with full-file scan and generation-bound promotion.</summary>
    Task<DocumentStoredContent> StoreAsync(DocumentUploadReservation reservation, ValidatedDocumentContent content, CancellationToken token);
    /// <summary>Repairs only a sealed committed version's exact clean SourceDeleted metadata acknowledgement.</summary>
    Task ReconcileCommittedAsync(DocumentStoredContent content, CancellationToken token);
    /// <summary>Confirms clean committed proof and reads only the stored generation.</summary>
    Task<ReadOnlyMemory<byte>> ReadAsync(DocumentStoredContent content, CancellationToken token);
}
/// <summary>Provides bounded private generation-selected bytes without signing.</summary>
public interface IProtectedDocumentGenerationReader
{
    /// <summary>Reads the exact immutable generation with a strict upper bound.</summary>
    Task<ReadOnlyMemory<byte>> ReadAsync(string bucket, string objectName, long generation, long maximumBytes, CancellationToken token);
}
/// <summary>Fails closed until the shared storage owner integrates a generation reader.</summary>
public sealed class UnavailableProtectedDocumentGenerationReader : IProtectedDocumentGenerationReader
{
    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> ReadAsync(string bucket, string objectName, long generation, long maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new DocumentAuthorityUnavailableException();
    }
}
/// <summary>Provides disabled storage without resolving owner provider or scanner dependencies.</summary>
public sealed class UnavailableProtectedDocumentStorage : IProtectedDocumentStorage
{
    /// <inheritdoc />
    public Task<DocumentStoredContent> StoreAsync(DocumentUploadReservation reservation, ValidatedDocumentContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new DocumentAuthorityUnavailableException();
    }
    /// <inheritdoc />
    public Task ReconcileCommittedAsync(DocumentStoredContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new DocumentAuthorityUnavailableException();
    }
    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> ReadAsync(DocumentStoredContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new DocumentAuthorityUnavailableException();
    }
}
/// <summary>Reports a safe concurrency or idempotency conflict.</summary>
public sealed class DocumentConflictException : Exception;
