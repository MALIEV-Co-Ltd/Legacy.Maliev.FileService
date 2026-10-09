using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Reads immutable exact-version evidence after current authority, association and clean-byte checks.</summary>
public sealed class CustomerDocumentRegistry(CustomerDocumentDbContext context, ICustomerDocumentAuthority authority,
    ICustomerDocumentAssociationValidator associations, ICustomerDocumentContentEvidence contentEvidence,
    CustomerDocumentRegistryOptions options, TimeProvider timeProvider) : ICustomerDocumentRegistry
{
    /// <summary>Reads the exact receipt and appends the successful transfer audit.</summary>
    public async Task<DocumentEvidenceReceipt?> ReadReceiptAsync(DocumentActor actor, int customerId, Guid documentId,
        Guid versionId, CancellationToken token)
    {
        try { return await ReadAuthorizedReceiptAsync(actor, customerId, documentId, versionId, token); }
        catch (NpgsqlException) when (!token.IsCancellationRequested) { throw new DocumentAuthorityUnavailableException(); }
        catch (Exception exception) when (IsProviderFailure(exception) && !token.IsCancellationRequested)
        { throw new DocumentAuthorityUnavailableException(); }
    }

    private static bool IsProviderFailure(Exception exception) => exception is NpgsqlException ||
        exception.InnerException is not null && IsProviderFailure(exception.InnerException);

    private async Task<DocumentEvidenceReceipt?> ReadAuthorizedReceiptAsync(DocumentActor actor, int customerId, Guid documentId,
        Guid versionId, CancellationToken token)
    {
        if (customerId <= 0 || documentId == Guid.Empty || versionId == Guid.Empty)
            throw new ArgumentException("Invalid document identity.");
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        var decision = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Read, token);
        Require(decision.Outcome);
        if (decision.ActorKind is null || !Enum.IsDefined(decision.ActorKind.Value) || string.IsNullOrWhiteSpace(decision.AuthorizedSubject))
            throw new DocumentAuthorityUnavailableException();
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.CustomerId == customerId, token);
        if (document is null || decision.ActorKind == DocumentActorKind.Member && document.Visibility == DocumentVisibility.Internal) return null;
        var version = await context.Versions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId && x.DocumentId == documentId && x.CustomerId == customerId, token);
        if (version is null) return null;
        if (!version.AssociationsSealed) throw new DocumentAuthorityUnavailableException();
        var links = await context.Associations.AsNoTracking().Where(x => x.VersionId == versionId).ToListAsync(token);
        if (links.Any(x => x.CustomerId != customerId || x.ResourceId <= 0 || !Enum.IsDefined(x.Kind))) throw new DocumentAuthorityDeniedException();
        Require(await associations.ValidateAsync(actor, customerId, links, token));
        Require(await contentEvidence.ValidateAsync(versionId, version.ContentSha256, token));
        var verification = await context.Verifications.AsNoTracking().Where(x => x.VersionId == versionId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(token);
        if (verification is not null && (!Enum.IsDefined(verification.Status) || verification.Revision <= 0 ||
            (verification.Status == VerificationStatus.PendingVerification
                ? verification.VerifiedBySubject is not null || verification.VerifiedAtUtc is not null
                : string.IsNullOrWhiteSpace(verification.VerifiedBySubject) || verification.VerifiedAtUtc is null || verification.VerifiedAtUtc.Value.Offset != TimeSpan.Zero)))
            throw new DocumentAuthorityUnavailableException();
        var receipt = new DocumentEvidenceReceipt(documentId, versionId, customerId, version.Kind, version.ContentSha256,
            links.Where(x => x.Kind == DocumentResourceKind.Quotation).Select(x => (int?)x.ResourceId).SingleOrDefault(),
            links.Where(x => x.Kind == DocumentResourceKind.Order).Select(x => x.ResourceId).Order().ToArray(),
            verification?.Status ?? VerificationStatus.PendingVerification,
            verification?.VerifiedBySubject, verification?.VerifiedAtUtc, verification?.Revision ?? version.Revision);
        context.Audits.Add(new DocumentAudit
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            VersionId = versionId,
            ActorSubject = decision.AuthorizedSubject,
            AtUtc = timeProvider.GetUtcNow(),
            Action = "ReceiptRead",
            Revision = receipt.Revision
        });
        await context.SaveChangesAsync(token);
        return receipt;
    }
    private static void Require(DocumentAuthorityOutcome outcome)
    {
        if (outcome == DocumentAuthorityOutcome.Unavailable) throw new DocumentAuthorityUnavailableException();
        if (outcome != DocumentAuthorityOutcome.Allowed) throw new DocumentAuthorityDeniedException();
    }
    /// <inheritdoc />
    public async Task FinalizeVersionAsync(DocumentActor actor, CustomerDocumentVersion version,
        IReadOnlyList<DocumentAssociation> links, CancellationToken token)
    {
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        if (version.Id == Guid.Empty || version.DocumentId == Guid.Empty || version.CustomerId <= 0 ||
            links.Any(x => x.VersionId != version.Id || x.CustomerId != version.CustomerId || x.ResourceId <= 0 || !Enum.IsDefined(x.Kind)))
            throw new ArgumentException("Invalid exact-version association set.");
        var decision = await authority.AuthorizeAsync(actor, version.CustomerId, CustomerDocumentPermissions.Write, token);
        Require(decision.Outcome);
        if (decision.ActorKind is null || !Enum.IsDefined(decision.ActorKind.Value) || string.IsNullOrWhiteSpace(decision.AuthorizedSubject))
            throw new DocumentAuthorityUnavailableException();
        Require(await associations.ValidateAsync(actor, version.CustomerId, links, token));
        if (version.ActorSubject != decision.AuthorizedSubject) throw new DocumentAuthorityDeniedException();
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == version.DocumentId && x.CustomerId == version.CustomerId, token)
            ?? throw new DocumentAuthorityDeniedException();
        if (decision.ActorKind == DocumentActorKind.Member && document.Visibility != DocumentVisibility.Customer)
            throw new DocumentAuthorityDeniedException();
        await context.InsertAndSealVersionAsync(version, links, token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentActor actor, int customerId, int offset, int limit, CancellationToken token)
    {
        ValidatePage(customerId, offset, limit);
        try
        {
            var decision = await AuthorizeReadAsync(actor, customerId, token);
            var query = context.Documents.AsNoTracking().Where(x => x.CustomerId == customerId && x.ArchivedAtUtc == null &&
                context.Versions.Any(v => v.DocumentId == x.Id && v.AssociationsSealed));
            if (decision.ActorKind == DocumentActorKind.Member) query = query.Where(x => x.Visibility == DocumentVisibility.Customer);
            var documents = await query.OrderBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(token);
            var result = new List<DocumentSummary>();
            foreach (var document in documents)
            {
                var version = await context.Versions.AsNoTracking().Where(x => x.DocumentId == document.Id && x.AssociationsSealed)
                    .OrderByDescending(x => x.VersionNumber).FirstAsync(token);
                if (await ReadReceiptAsync(actor, customerId, document.Id, version.Id, token) is not null)
                    result.Add(new(document.Id, customerId, document.Kind, document.Title, document.Visibility, document.Revision));
            }
            return result;
        }
        catch (NpgsqlException) when (!token.IsCancellationRequested) { throw new DocumentAuthorityUnavailableException(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentVersionSummary>?> ReadVersionsAsync(DocumentActor actor, int customerId,
        Guid documentId, int offset, int limit, CancellationToken token)
    {
        ValidatePage(customerId, offset, limit);
        if (documentId == Guid.Empty) throw new ArgumentException("Invalid document identity.");
        try
        {
            var decision = await AuthorizeReadAsync(actor, customerId, token);
            var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.CustomerId == customerId, token);
            if (document is null || decision.ActorKind == DocumentActorKind.Member && document.Visibility == DocumentVisibility.Internal) return null;
            var versions = await context.Versions.AsNoTracking().Where(x => x.DocumentId == documentId && x.AssociationsSealed)
                .OrderByDescending(x => x.VersionNumber).Skip(offset).Take(limit).ToListAsync(token);
            var result = new List<DocumentVersionSummary>();
            foreach (var version in versions)
            {
                var receipt = await ReadReceiptAsync(actor, customerId, documentId, version.Id, token);
                if (receipt is not null)
                    result.Add(new(documentId, version.Id, version.VersionNumber, receipt.Kind, receipt.ContentSha256,
                        version.CreatedAtUtc, receipt.VerificationStatus, receipt.VerifiedBySubject, receipt.VerifiedAtUtc, receipt.Revision));
            }
            return result;
        }
        catch (NpgsqlException) when (!token.IsCancellationRequested) { throw new DocumentAuthorityUnavailableException(); }
    }

    private async Task<DocumentAuthorityDecision> AuthorizeReadAsync(DocumentActor actor, int customerId, CancellationToken token)
    {
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        var decision = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Read, token);
        Require(decision.Outcome);
        if (decision.ActorKind is null || !Enum.IsDefined(decision.ActorKind.Value) || string.IsNullOrWhiteSpace(decision.AuthorizedSubject))
            throw new DocumentAuthorityUnavailableException();
        return decision;
    }

    private static void ValidatePage(int customerId, int offset, int limit)
    {
        if (customerId <= 0 || offset is < 0 or > 10000 || limit is < 1 or > 50)
            throw new ArgumentException("Invalid bounded page.");
    }
}
