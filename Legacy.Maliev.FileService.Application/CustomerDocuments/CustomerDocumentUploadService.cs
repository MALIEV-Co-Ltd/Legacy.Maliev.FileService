using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.Extensions.Options;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Coordinates authorized metadata-only immutable uploads.</summary>
public sealed class CustomerDocumentUploadService(ICustomerDocumentAuthority authority, ICustomerDocumentAssociationValidator associations, IProtectedDocumentStore store, IProtectedDocumentStorage storage, IOptions<CustomerDocumentOptions> options)
{
    /// <summary>Uploads one bounded clean immutable version without signing a URL.</summary>
    public async Task<DocumentVersionReceipt> UploadAsync(DocumentActor actor, int customerId, DocumentUploadRequest request, IUploadFile file, string idempotencyKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await UploadCoreAsync(actor, customerId, request, file, idempotencyKey, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (DocumentContentException) { throw; }
        catch (DocumentConflictException) { throw; }
        catch (DocumentAuthorityDeniedException) { throw; }
        catch (Exception) { throw new DocumentAuthorityUnavailableException(); }
    }
    private async Task<DocumentVersionReceipt> UploadCoreAsync(DocumentActor actor, int customerId, DocumentUploadRequest request, IUploadFile file, string idempotencyKey, CancellationToken token)
    {
        if (!options.Value.Enabled) throw new DocumentAuthorityUnavailableException();
        if (customerId <= 0 || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl) ||
            !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Visibility) || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 250 ||
            request.DocumentId == Guid.Empty || request.Associations.Count > 100) throw new DocumentContentException(400);
        var decision = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Write, token);
        RequireAuthority(decision);
        if (decision.ActorKind == DocumentActorKind.Member && request.Visibility != DocumentVisibility.Customer) throw new DocumentAuthorityDeniedException();
        var canonical = request.Associations.Select(x => new DocumentAssociation { Kind = x.Kind, ResourceId = x.ResourceId, CustomerId = customerId }).OrderBy(x => x.Kind).ThenBy(x => x.ResourceId).ToArray();
        if (canonical.Any(x => !Enum.IsDefined(x.Kind) || x.ResourceId <= 0) || canonical.Select(x => (x.Kind, x.ResourceId)).Distinct().Count() != canonical.Length) throw new DocumentContentException(400);
        RequireOutcome(await associations.ValidateAsync(actor, customerId, canonical, token));
        var captured = request with { Associations = canonical };
        var content = await DocumentContentValidator.ReadAsync(file, token);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { customerId, request.DocumentId, request.Kind, request.Title, request.Visibility, request.ExpectedRevision, content.Sha256, content.ContentType, content.FileName, Associations = canonical.Select(x => new { x.Kind, x.ResourceId }) }))));
        var reservation = await store.ReserveAsync(customerId, decision.AuthorizedSubject!, idempotencyKey, fingerprint, captured, token);
        if (reservation.Replay is not null)
        {
            var prior = await store.FindAsync(customerId, reservation.DocumentId, reservation.VersionId, decision.ActorKind!.Value, token);
            if (prior is null || prior.Sha256 != reservation.Replay.ContentSha256 || prior.Sha256 != content.Sha256) throw new DocumentAuthorityUnavailableException();
            await storage.ReconcileCommittedAsync(prior, token);
            await storage.ReadAsync(prior, token);
            return reservation.Replay;
        }
        try
        {
            var stored = await storage.StoreAsync(reservation, content, token);
            return await store.FinalizeAsync(actor, reservation, captured, stored, decision.AuthorizedSubject!, token);
        }
        catch
        {
            using var checkpoint = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await store.MarkUnknownAsync(reservation.OperationId, checkpoint.Token).WaitAsync(checkpoint.Token); }
            catch (Exception) { /* The original durable pending reservation remains retained. */ }
            throw;
        }
    }
    internal static void RequireAuthority(DocumentAuthorityDecision decision)
    {
        RequireOutcome(decision.Outcome);
        if (decision.ActorKind is null || !Enum.IsDefined(decision.ActorKind.Value) || string.IsNullOrWhiteSpace(decision.AuthorizedSubject)) throw new DocumentAuthorityUnavailableException();
    }
    internal static void RequireOutcome(DocumentAuthorityOutcome outcome)
    {
        if (outcome == DocumentAuthorityOutcome.Denied) throw new DocumentAuthorityDeniedException();
        if (outcome != DocumentAuthorityOutcome.Allowed) throw new DocumentAuthorityUnavailableException();
    }
}
