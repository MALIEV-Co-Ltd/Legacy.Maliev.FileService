using Legacy.Maliev.FileService.Domain.CustomerDocuments;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Verifies exact clean externally signed bytes using current active staff and canonical scope.</summary>
public sealed class NdaVerificationService(INdaAuthority authority, INdaRepository repository,
    ICustomerDocumentContentEvidence evidence, NdaOptions options, TimeProvider clock)
{
    /// <summary>Reads employee-only lifecycle indicators; historical confidentiality is never inferred from dates.</summary>
    public async Task<NdaAgreementSummary> ReadAsync(DocumentActor actor, int customerId, Guid documentId, Guid? versionId, CancellationToken token)
    {
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        if (customerId <= 0 || documentId == Guid.Empty || versionId == Guid.Empty) throw new ArgumentException("Invalid agreement identity.");
        var viewer = NdaAuthorityGuard.Subject(await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Read, token), true);
        NdaAuthorityGuard.Require(await authority.ValidateActiveEmployeeAsync(viewer, token));
        var readback = await repository.ReadAgreementAsync(customerId, documentId, versionId, token) ?? throw new NdaNotFoundException();
        var record = readback.Record;
        if (record.CustomerId != customerId || record.DocumentId != documentId || versionId is not null && record.VersionId != versionId)
            throw new DocumentAuthorityDeniedException();
        if (!record.CoverageSealed) throw new DocumentAuthorityUnavailableException();
        var now = clock.GetUtcNow();
        return new(record.Id, record.DocumentId, record.VersionId, record.VerificationRevision, record.EffectiveAtUtc,
            record.ExpiresAtUtc, record.RenewalAtUtc, record.SurvivalKind, record.SurvivalEndsAtUtc, record.ResponsibleEmployeeSubject,
            NdaLifecycle.CalendarStatus(record, now, readback.Superseded), NdaLifecycle.ObligationStatus(record, now));
    }
    /// <summary>Appends immutable evidence with optimistic concurrency and no release override.</summary>
    public async Task<NdaVerificationReceipt> VerifyAsync(DocumentActor actor, int customerId, Guid documentId,
        NdaVerificationRequest request, CancellationToken token)
    {
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        if (customerId <= 0 || documentId == Guid.Empty) throw new ArgumentException("Invalid customer document identity.");
        var verifier = NdaAuthorityGuard.Subject(await authority.AuthorizeAsync(actor, customerId, CustomerDocumentPermissions.Verify, token), true);
        Validate(request, customerId);
        NdaAuthorityGuard.Require(await authority.ValidateActiveEmployeeAsync(verifier, token));
        NdaAuthorityGuard.Require(await authority.ValidateActiveEmployeeAsync(request.ResponsibleEmployeeSubject, token));
        var canonical = await authority.ResolveCoverageAsync(actor, customerId, request.Coverage, request.VersionId, token);
        NdaAuthorityGuard.Scope(canonical, customerId, request.Coverage, request.VersionId);
        // Signed-version commercial associations are not legal attestations. Review the requested legal scope
        // and its owner-confirmed original-order expansion independently from the version ownership proof.
        var legalScope = await authority.ResolveCoverageAsync(actor, customerId, request.Coverage, null, token);
        NdaAuthorityGuard.Scope(legalScope, customerId, request.Coverage, null);
        var target = await repository.ReadVerificationTargetAsync(customerId, documentId, request.VersionId, token) ?? throw new NdaNotFoundException();
        if (target.CustomerId != customerId || target.DocumentId != documentId || target.VersionId != request.VersionId) throw new DocumentAuthorityDeniedException();
        if (target.Kind != DocumentKind.Nda) throw new NdaValidationException();
        if (target.Revision != request.ExpectedRevision) throw new NdaRevisionConflictException();
        NdaAuthorityGuard.Require(await evidence.ValidateAsync(target.VersionId, target.ContentSha256, token));
        var now = clock.GetUtcNow();
        var record = new NdaRecord { Id = Guid.NewGuid(), DocumentId = documentId, VersionId = request.VersionId, CustomerId = customerId,
            PartyOne = request.PartyOne.Trim(), PartyTwo = request.PartyTwo.Trim(), EffectiveAtUtc = request.EffectiveAtUtc,
            ExpiresAtUtc = request.ExpiresAtUtc, RenewalAtUtc = request.RenewalAtUtc, SurvivalKind = request.SurvivalKind,
            SurvivalEndsAtUtc = request.SurvivalEndsAtUtc, ResponsibleEmployeeSubject = request.ResponsibleEmployeeSubject,
            VerifiedBySubject = verifier, VerifiedAtUtc = now };
        record = await repository.AppendVerificationAsync(record, legalScope.Resources, request.ExpectedRevision, request.Reason.Trim(), token);
        return new(record.Id, record.DocumentId, record.VersionId, record.VerificationRevision,
            NdaLifecycle.CalendarStatus(record, now, false), NdaLifecycle.ObligationStatus(record, now));
    }
    private static void Validate(NdaVerificationRequest r, int customerId)
    {
        static bool Utc(DateTimeOffset? date) => date is null || date.Value.Offset == TimeSpan.Zero;
        if (r.VersionId == Guid.Empty || r.ExpectedRevision <= 0 || string.IsNullOrWhiteSpace(r.PartyOne) || string.IsNullOrWhiteSpace(r.PartyTwo)
            || r.PartyOne.Trim().Equals(r.PartyTwo.Trim(), StringComparison.OrdinalIgnoreCase) || r.PartyOne.Length > 500 || r.PartyTwo.Length > 500
            || string.IsNullOrWhiteSpace(r.ResponsibleEmployeeSubject) || r.ResponsibleEmployeeSubject.Length > 200
            || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 2000 || !Enum.IsDefined(r.SurvivalKind)
            || r.EffectiveAtUtc == default || !Utc(r.EffectiveAtUtc) || !Utc(r.ExpiresAtUtc) || !Utc(r.RenewalAtUtc) || !Utc(r.SurvivalEndsAtUtc)
            || r.ExpiresAtUtc <= r.EffectiveAtUtc || r.RenewalAtUtc < r.EffectiveAtUtc
            || r.SurvivalKind == NdaSurvivalKind.Finite && (r.SurvivalEndsAtUtc is null || r.SurvivalEndsAtUtc < (r.ExpiresAtUtc ?? r.EffectiveAtUtc))
            || r.SurvivalKind != NdaSurvivalKind.Finite && r.SurvivalEndsAtUtc is not null
            || r.Coverage is null || r.Coverage.Count is 0 or > 100 || r.Coverage.Distinct().Count() != r.Coverage.Count
            || r.Coverage.Any(x => x.ResourceId <= 0 || !Enum.IsDefined(x.Kind) || x.Kind == DocumentResourceKind.Customer && x.ResourceId != customerId))
            throw new NdaValidationException();
    }
}
