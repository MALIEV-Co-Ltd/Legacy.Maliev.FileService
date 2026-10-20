using System.Data;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Persists protected versions and upload identities in the real registry database.</summary>
public sealed class ProtectedDocumentStore(CustomerDocumentDbContext db, IStorageMoveJournal journal, ICustomerDocumentRegistry registry, TimeProvider clock) : IProtectedDocumentStore
{
    /// <inheritdoc />
    public async Task<DocumentUploadReservation> ReserveAsync(int customerId, string subject, string key, string fingerprint, DocumentUploadRequest request, CancellationToken token)
    {
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({customerId})", token);
        var previous = await db.Set<ProtectedDocumentUploadCheckpoint>().SingleOrDefaultAsync(x => x.CustomerId == customerId && x.Subject == subject && x.KeyHash == keyHash, token);
        if (previous is not null)
        {
            if (previous.Fingerprint != fingerprint) throw new DocumentConflictException();
            if (previous.State != "Completed") throw new DocumentConflictException();
            var version = await db.Versions.SingleAsync(x => x.Id == previous.VersionId && x.CustomerId == customerId, token);
            await transaction.CommitAsync(token);
            return Reservation(previous, new(previous.DocumentId, previous.VersionId, customerId, previous.VersionNumber, version.ContentSha256, previous.CompletedRevision!.Value));
        }
        var documentId = request.DocumentId ?? Guid.NewGuid();
        var document = await db.Documents.SingleOrDefaultAsync(x => x.Id == documentId && x.CustomerId == customerId, token);
        if (request.DocumentId is not null && (document is null || document.ArchivedAtUtc is not null)) throw new DocumentAuthorityDeniedException();
        if (document is not null && (document.Kind != request.Kind || document.Visibility != request.Visibility || request.ExpectedRevision != document.Revision)) throw new DocumentConflictException();
        var reservedMaximum = await db.Set<ProtectedDocumentUploadCheckpoint>().Where(x => x.DocumentId == documentId).MaxAsync(x => (int?)x.VersionNumber, token) ?? 0;
        var finalizedMaximum = await db.Versions.Where(x => x.DocumentId == documentId && x.CustomerId == customerId).MaxAsync(x => (int?)x.VersionNumber, token) ?? 0;
        var number = checked(Math.Max(reservedMaximum, finalizedMaximum) + 1);
        var checkpoint = new ProtectedDocumentUploadCheckpoint { OperationId = Guid.NewGuid(), CustomerId = customerId, Subject = subject, KeyHash = keyHash, Fingerprint = fingerprint, DocumentId = documentId, VersionId = Guid.NewGuid(), VersionNumber = number };
        if (document is null) db.Documents.Add(new CustomerDocument { Id = documentId, CustomerId = customerId, Kind = request.Kind, Title = request.Title, Visibility = request.Visibility });
        db.Set<ProtectedDocumentUploadCheckpoint>().Add(checkpoint);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Reservation(checkpoint);
    }
    /// <inheritdoc />
    public async Task<DocumentVersionReceipt> FinalizeAsync(DocumentActor actor, DocumentUploadReservation reservation, DocumentUploadRequest request, DocumentStoredContent content, string subject, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({reservation.CustomerId})", token);
        var checkpoint = await db.Set<ProtectedDocumentUploadCheckpoint>().SingleAsync(x => x.OperationId == reservation.OperationId, token);
        if (checkpoint.State != "Pending" || checkpoint.Subject != subject || checkpoint.Fingerprint != reservation.Fingerprint || checkpoint.VersionId != reservation.VersionId || checkpoint.DocumentId != reservation.DocumentId || checkpoint.CustomerId != reservation.CustomerId || checkpoint.VersionNumber != reservation.VersionNumber || content.ScanOperationId != reservation.OperationId || content.ObjectName != $"customer-documents/{reservation.CustomerId}/{reservation.DocumentId:N}/{reservation.VersionId:N}/original") throw new DocumentConflictException();
        var document = await db.Documents.FromSqlInterpolated($"SELECT * FROM \"CustomerDocument\" WHERE \"Id\"={reservation.DocumentId} AND \"CustomerId\"={reservation.CustomerId} FOR UPDATE").SingleAsync(token);
        if (document.ArchivedAtUtc is not null || document.Kind != request.Kind || document.Visibility != request.Visibility || request.DocumentId is not null && request.ExpectedRevision != document.Revision) throw new DocumentConflictException();
        var version = new CustomerDocumentVersion { Id = reservation.VersionId, DocumentId = reservation.DocumentId, CustomerId = reservation.CustomerId, Kind = request.Kind, VersionNumber = reservation.VersionNumber, ContentSha256 = content.Sha256, ActorSubject = subject, CreatedAtUtc = clock.GetUtcNow(), StorageBucket = content.Bucket, StorageObjectName = content.ObjectName, StorageGeneration = content.Generation, ContentSize = content.Size, ContentType = content.ContentType, OriginalFileName = content.FileName, ScanOperationId = content.ScanOperationId, ScanSourceGeneration = content.SourceGeneration };
        var links = request.Associations.Select(x => new DocumentAssociation { VersionId = version.Id, Kind = x.Kind, ResourceId = x.ResourceId, CustomerId = version.CustomerId }).ToArray();
        await registry.FinalizeVersionAsync(actor, version, links, token);
        document.Revision++;
        checkpoint.State = "Completed";
        checkpoint.CompletedRevision = document.Revision;
        db.Audits.Add(new DocumentAudit { Id = Guid.NewGuid(), DocumentId = document.Id, VersionId = version.Id, ActorSubject = subject, AtUtc = clock.GetUtcNow(), Action = "Upload", Revision = document.Revision });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        // A lost journal commit response leaves immutable registry evidence retained but unreadable.
        await journal.MetadataCommittedAsync(content.ScanOperationId, token);
        return new(document.Id, version.Id, document.CustomerId, version.VersionNumber, version.ContentSha256, document.Revision);
    }
    /// <inheritdoc />
    public Task MarkUnknownAsync(Guid operationId, CancellationToken token) => db.Set<ProtectedDocumentUploadCheckpoint>().Where(x => x.OperationId == operationId && x.State == "Pending").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "Unknown"), token);
    /// <inheritdoc />
    public async Task<DocumentStoredContent?> FindAsync(int customerId, Guid documentId, Guid versionId, DocumentActorKind actorKind, CancellationToken token)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.CustomerId == customerId, token);
        if (document is null || actorKind == DocumentActorKind.Member && document.Visibility != DocumentVisibility.Customer) return null;
        var version = await db.Versions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId && x.DocumentId == documentId && x.CustomerId == customerId && x.AssociationsSealed, token);
        return version is null ? null : new(version.StorageBucket, version.StorageObjectName, version.StorageGeneration, version.ScanSourceGeneration, version.ScanOperationId, version.ContentSize, version.ContentSha256, version.ContentType, version.OriginalFileName);
    }
    /// <inheritdoc />
    public async Task AuditReadAsync(int customerId, Guid documentId, Guid versionId, string subject, long authorizedRevision, CancellationToken token)
    {
        if (authorizedRevision <= 0 || !await db.Versions.AnyAsync(x => x.Id == versionId && x.DocumentId == documentId && x.CustomerId == customerId && x.AssociationsSealed, token)) throw new DocumentAuthorityUnavailableException();
        db.Audits.Add(new DocumentAudit { Id = Guid.NewGuid(), DocumentId = documentId, VersionId = versionId, ActorSubject = subject, AtUtc = clock.GetUtcNow(), Action = "Download", Revision = authorizedRevision });
        await db.SaveChangesAsync(token);
    }
    /// <inheritdoc />
    public async Task ArchiveAsync(int customerId, Guid documentId, long expectedRevision, string reason, string subject, DocumentActorKind actorKind, CancellationToken token)
    {
        if (!Enum.IsDefined(actorKind)) throw new DocumentAuthorityUnavailableException();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({customerId})", token);
        var document = await db.Documents.FromSqlInterpolated($"SELECT * FROM \"CustomerDocument\" WHERE \"Id\"={documentId} AND \"CustomerId\"={customerId} FOR UPDATE").SingleOrDefaultAsync(token) ?? throw new DocumentAuthorityDeniedException();
        if (actorKind == DocumentActorKind.Member && document.Visibility != DocumentVisibility.Customer) throw new DocumentAuthorityDeniedException();
        if (document.Revision != expectedRevision || document.ArchivedAtUtc is not null || await db.Set<CustomerDocumentLegalHold>().AnyAsync(x => x.DocumentId == documentId && x.CustomerId == customerId, token)) throw new DocumentConflictException();
        document.ArchivedAtUtc = clock.GetUtcNow();
        document.Revision++;
        db.Audits.Add(new DocumentAudit { Id = Guid.NewGuid(), DocumentId = documentId, ActorSubject = subject, AtUtc = clock.GetUtcNow(), Action = "Archive", Reason = reason, Revision = document.Revision });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    private static DocumentUploadReservation Reservation(ProtectedDocumentUploadCheckpoint value, DocumentVersionReceipt? replay = null) => new(value.OperationId, value.DocumentId, value.VersionId, value.CustomerId, value.VersionNumber, value.Fingerprint, replay);
}
