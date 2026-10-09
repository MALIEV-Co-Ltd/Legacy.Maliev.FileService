using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Legacy.Maliev.FileService.Data.CustomerDocuments;

/// <summary>Appends verification evidence without rewriting captured bytes or associations.</summary>
public sealed class DocumentEvidenceVerificationService(CustomerDocumentDbContext context,
    ICustomerDocumentRegistry registry, ICustomerDocumentAuthority authority,
    IDocumentVerificationEmployeeAuthority employees, CustomerDocumentRegistryOptions options, TimeProvider clock)
    : IDocumentEvidenceVerificationService
{
    /// <inheritdoc />
    public async Task<DocumentEvidenceReceipt?> VerifyAsync(DocumentActor actor, int customerId, Guid documentId, Guid versionId,
        DocumentEvidenceVerificationRequest request, CancellationToken token)
    {
        if (customerId <= 0 || documentId == Guid.Empty || versionId == Guid.Empty || request.ExpectedVerificationRevision <= 0 ||
            request.Status is not ("Verified" or "Rejected") || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1024)
            throw new ArgumentException("Invalid exact verification request.");
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        try
        {
            var initial = await AuthorizeEmployeeAsync(actor, customerId, token);
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var version = await context.Versions.FromSqlInterpolated($"SELECT * FROM \"CustomerDocumentVersion\" WHERE \"Id\"={versionId} AND \"DocumentId\"={documentId} AND \"CustomerId\"={customerId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (version is null) return null;
            var current = await AuthorizeEmployeeAsync(actor, customerId, token);
            if (current.AuthorizedSubject != initial.AuthorizedSubject) throw new DocumentAuthorityDeniedException();
            Require(await employees.ValidateActiveEmployeeAsync(current.AuthorizedSubject!, token));
            var read = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Read, token);
            Require(read.Outcome);
            if (read.ActorKind != DocumentActorKind.Employee || read.AuthorizedSubject != current.AuthorizedSubject)
                throw new DocumentAuthorityDeniedException();
            var receipt = await registry.ReadReceiptAsync(actor, customerId, documentId, versionId, token);
            if (receipt is null) return null;
            if (receipt.Kind == DocumentKind.Nda) throw new DocumentVerificationKindException();
            if (receipt.Revision != request.ExpectedVerificationRevision) throw new DocumentVerificationConflictException();
            var revision = checked(receipt.Revision + 1);
            var status = request.Status == "Verified" ? VerificationStatus.Verified : VerificationStatus.Rejected;
            var at = clock.GetUtcNow().ToUniversalTime();
            context.Verifications.Add(new DocumentVerificationEvidence
            {
                VersionId = versionId,
                Revision = revision,
                Status = status,
                VerifiedBySubject = current.AuthorizedSubject,
                VerifiedAtUtc = at
            });
            context.Audits.Add(new DocumentAudit
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                VersionId = versionId,
                ActorSubject = current.AuthorizedSubject!,
                AtUtc = at,
                Action = "VerificationDecided",
                Reason = request.Reason,
                Revision = revision
            });
            await context.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return receipt with { VerificationStatus = status, VerifiedBySubject = current.AuthorizedSubject, VerifiedAtUtc = at, Revision = revision };
        }
        catch (Exception exception) when (!token.IsCancellationRequested && IsProviderFailure(exception))
        { throw new DocumentAuthorityUnavailableException(); }
    }

    private async Task<DocumentAuthorityDecision> AuthorizeEmployeeAsync(DocumentActor actor, int customerId, CancellationToken token)
    {
        var decision = await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Verify, token);
        Require(decision.Outcome);
        if (decision.ActorKind is null || !Enum.IsDefined(decision.ActorKind.Value) || string.IsNullOrWhiteSpace(decision.AuthorizedSubject))
            throw new DocumentAuthorityUnavailableException();
        if (decision.ActorKind != DocumentActorKind.Employee) throw new DocumentAuthorityDeniedException();
        return decision;
    }

    private static void Require(DocumentAuthorityOutcome outcome)
    {
        if (outcome == DocumentAuthorityOutcome.Unavailable) throw new DocumentAuthorityUnavailableException();
        if (outcome != DocumentAuthorityOutcome.Allowed) throw new DocumentAuthorityDeniedException();
    }

    private static bool IsProviderFailure(Exception exception) => exception is NpgsqlException ||
        exception.InnerException is not null && IsProviderFailure(exception.InnerException);
}
