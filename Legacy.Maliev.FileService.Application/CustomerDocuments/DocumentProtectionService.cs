using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Enforces surviving NDA obligations across canonical derivative resources.</summary>
public sealed class DocumentProtectionService(INdaAuthority authority, INdaRepository repository, NdaOptions options, TimeProvider clock) : IDocumentProtectionService
{
    /// <inheritdoc />
    public async Task<ProtectionDecision> EvaluateAsync(DocumentActor actor, int customerId, ProtectionRequest request, CancellationToken token)
    {
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        if (customerId <= 0 || request.ResourceId <= 0 || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Action)
            || request.Kind == DocumentResourceKind.Customer && request.ResourceId != customerId || request.VersionId == Guid.Empty)
            throw new ArgumentException("Invalid protection scope.");
        var permission = request.Action == ProtectionAction.Read ? CustomerDocumentPermissions.Read : CustomerDocumentPermissions.EvaluateProtection;
        var decision = await authority.AuthorizeAsync(actor, customerId, permission, token);
        NdaAuthorityGuard.Subject(decision);
        NdaCoverageRequest[] requested = [new(request.Kind, request.ResourceId)];
        var canonical = await authority.ResolveProtectionCoverageAsync(actor, customerId, requested, request.VersionId, token);
        NdaAuthorityGuard.Scope(canonical, customerId, requested, request.VersionId);
        if (request.VersionId is not null)
        {
            if (!canonical.ConfirmedResourceVersionAssociation || canonical.ConfirmedVersionVisibility is null)
                throw new DocumentAuthorityUnavailableException();
            if (decision.ActorKind == DocumentActorKind.Member && canonical.ConfirmedVersionVisibility == DocumentVisibility.Internal)
                throw new DocumentAuthorityDeniedException();
        }
        var records = await repository.ReadCoveredAgreementsAsync(customerId, canonical.Resources, request.VersionId, token);
        if (records.Any(x => x.CustomerId != customerId)) throw new DocumentAuthorityUnavailableException();
        var protectedWork = records.Count > 0;
        var obligation = records.Any(x => NdaLifecycle.ObligationStatus(x, clock.GetUtcNow()) == ConfidentialityObligationStatus.ReviewRequired)
            ? ConfidentialityObligationStatus.ReviewRequired : protectedWork ? ConfidentialityObligationStatus.Protected : ConfidentialityObligationStatus.Released;
        if (request.Action == ProtectionAction.Read) return new(true, protectedWork, obligation, "ScopedRead");
        if (canonical.ConfirmedVersionVisibility == DocumentVisibility.Internal)
            return new(false, protectedWork, obligation, "InternalVisibility");
        if (protectedWork) return new(false, true, obligation, "ConfidentialitySurvives");
        if (request.Action is ProtectionAction.PublishSocial or ProtectionAction.PublicExport)
        {
            if (canonical.ConfirmedSocialConsent is null) throw new DocumentAuthorityUnavailableException();
            if (!canonical.ConfirmedSocialConsent.Value) return new(false, false, obligation, "ConsentRequired");
        }
        return new(true, false, obligation, "NoCoveredObligation");
    }
}
