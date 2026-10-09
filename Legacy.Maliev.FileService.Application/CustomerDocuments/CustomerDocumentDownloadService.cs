using Microsoft.Extensions.Options;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Rechecks current customer access before fully buffering a private attachment.</summary>
public sealed class CustomerDocumentDownloadService(ICustomerDocumentAuthority authority, IProtectedDocumentStore store, IProtectedDocumentStorage storage, ICustomerDocumentRegistry registry, IDocumentProtectionService protection, IOptions<CustomerDocumentOptions> options)
{
    /// <summary>Reads clean exact-generation bytes without producing a bearer URL.</summary>
    public async Task<DocumentDownload?> ReadAsync(DocumentActor actor, int customerId, Guid documentId, Guid versionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await ReadCoreAsync(actor, customerId, documentId, versionId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (DocumentContentException) { throw; }
        catch (DocumentAuthorityDeniedException) { throw; }
        catch (Exception) { throw new DocumentAuthorityUnavailableException(); }
    }
    private async Task<DocumentDownload?> ReadCoreAsync(DocumentActor actor, int customerId, Guid documentId, Guid versionId, CancellationToken token)
    {
        if (!options.Value.Enabled) throw new DocumentAuthorityUnavailableException();
        if (customerId <= 0 || documentId == Guid.Empty || versionId == Guid.Empty) throw new DocumentContentException(400);
        var decision = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Read, token);
        CustomerDocumentUploadService.RequireAuthority(decision);
        var receipt = await registry.ReadReceiptAsync(actor, customerId, documentId, versionId, token);
        if (receipt is null) return null;
        var policy = await protection.EvaluateAsync(actor, customerId, new(DocumentResourceKind.Customer, customerId, ProtectionAction.Read, VersionId: versionId), token);
        if (!policy.Allowed) throw new DocumentAuthorityDeniedException();
        var content = await store.FindAsync(customerId, documentId, versionId, decision.ActorKind!.Value, token);
        if (content is null) return null;
        var bytes = await storage.ReadAsync(content, token);
        await store.AuditReadAsync(customerId, documentId, versionId, decision.AuthorizedSubject!, receipt.Revision, token);
        return new(bytes, content.ContentType, content.FileName);
    }
    /// <summary>Archives with customer authority, explicit reason and concurrency; originals remain retained.</summary>
    public async Task ArchiveAsync(DocumentActor actor, int customerId, Guid documentId, long expectedRevision, string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await ArchiveCoreAsync(actor, customerId, documentId, expectedRevision, reason, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (DocumentContentException) { throw; }
        catch (DocumentConflictException) { throw; }
        catch (DocumentAuthorityDeniedException) { throw; }
        catch (Exception) { throw new DocumentAuthorityUnavailableException(); }
    }
    private async Task ArchiveCoreAsync(DocumentActor actor, int customerId, Guid documentId, long expectedRevision, string reason, CancellationToken token)
    {
        if (!options.Value.Enabled) throw new DocumentAuthorityUnavailableException();
        if (customerId <= 0 || documentId == Guid.Empty || expectedRevision <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 1024) throw new DocumentContentException(400);
        var decision = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Archive, token);
        CustomerDocumentUploadService.RequireAuthority(decision);
        await store.ArchiveAsync(customerId, documentId, expectedRevision, reason, decision.AuthorizedSubject!, decision.ActorKind!.Value, token);
    }
}
