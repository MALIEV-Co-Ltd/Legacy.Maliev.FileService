using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;


namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class NdaLifecycleTests
{
    [Fact]
    public async Task ClientSocialConsentCannotOverrideCurrentOwnerConsent()
    {
        var service = new DocumentProtectionService(new SyntheticAuthority(DocumentActorKind.Employee), new SyntheticRepository(), new NdaOptions { Enabled = true }, TimeProvider.System);
        var result = await service.EvaluateAsync(Actor(), 23, new(DocumentResourceKind.Order, 17, ProtectionAction.PublishSocial, true), default);
        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task UnrelatedVersionCannotBePubliclyExportedUsingAnUncoveredOrder()
    {
        var service = new DocumentProtectionService(new SyntheticAuthority(DocumentActorKind.Employee), new SyntheticRepository(), new NdaOptions { Enabled = true }, TimeProvider.System);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => service.EvaluateAsync(Actor(), 23,
            new(DocumentResourceKind.Order, 17, ProtectionAction.PublicExport, true, Guid.NewGuid()), default));
    }
    [Theory]
    [InlineData(NdaSurvivalKind.Unknown)]
    [InlineData(NdaSurvivalKind.Indefinite)]
    [InlineData(NdaSurvivalKind.Finite)]
    public void ExpiredAgreementNeverReleasesObligation(NdaSurvivalKind survival)
    {
        var record = new NdaRecord { EffectiveAtUtc = DateTimeOffset.UtcNow.AddYears(-2),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(-1), SurvivalKind = survival,
            SurvivalEndsAtUtc = DateTimeOffset.UtcNow.AddMonths(-1) };
        Assert.Equal(AgreementCalendarStatus.Expired, NdaLifecycle.CalendarStatus(record, DateTimeOffset.UtcNow, false));
        Assert.NotEqual(ConfidentialityObligationStatus.Released, NdaLifecycle.ObligationStatus(record, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SupersededAgreementRetainsProtection()
    {
        var record = new NdaRecord { EffectiveAtUtc = DateTimeOffset.UtcNow.AddYears(-1), SurvivalKind = NdaSurvivalKind.Indefinite };
        Assert.Equal(AgreementCalendarStatus.Superseded, NdaLifecycle.CalendarStatus(record, DateTimeOffset.UtcNow, true));
        Assert.Equal(ConfidentialityObligationStatus.Protected, NdaLifecycle.ObligationStatus(record, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task MemberCannotVerifyEvenWithGenericPermission()
    {
        var authority = new SyntheticAuthority(DocumentActorKind.Member);
        var service = new NdaVerificationService(authority, new SyntheticRepository(), new SyntheticEvidence(), new NdaOptions { Enabled = true }, TimeProvider.System);
        await Assert.ThrowsAsync<DocumentAuthorityDeniedException>(() => service.VerifyAsync(Actor(), 23, Guid.NewGuid(), Request(), default));
    }

    [Fact]
    public async Task CurrentStaffUnavailablePreventsVerification()
    {
        var authority = new SyntheticAuthority(DocumentActorKind.Employee) { StaffOutcome = DocumentAuthorityOutcome.Unavailable };
        var service = new NdaVerificationService(authority, new SyntheticRepository(), new SyntheticEvidence(), new NdaOptions { Enabled = true }, TimeProvider.System);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => service.VerifyAsync(Actor(), 23, Guid.NewGuid(), Request(), default));
    }

    [Fact]
    public async Task InvalidExpiryCannotBeVerified()
    {
        var service = new NdaVerificationService(new SyntheticAuthority(DocumentActorKind.Employee), new SyntheticRepository(), new SyntheticEvidence(), new NdaOptions { Enabled = true }, TimeProvider.System);
        var request = Request() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(-1) };
        await Assert.ThrowsAsync<NdaValidationException>(() => service.VerifyAsync(Actor(), 23, Guid.NewGuid(), request, default));
    }

    [Fact]
    public async Task StaleRevisionIsRejectedBeforeAppendingEvidence()
    {
        var service = new NdaVerificationService(new SyntheticAuthority(DocumentActorKind.Employee), new SyntheticRepository(), new SyntheticEvidence(), new NdaOptions { Enabled = true }, TimeProvider.System);
        await Assert.ThrowsAsync<NdaRevisionConflictException>(() => service.VerifyAsync(Actor(), 23, Guid.NewGuid(), Request() with { ExpectedRevision = 9 }, default));
    }

    [Theory]
    [InlineData(ProtectionAction.ExternalShare)]
    [InlineData(ProtectionAction.PublishSocial)]
    [InlineData(ProtectionAction.PublicExport)]
    public async Task CanonicallyCoveredDerivativesDenyDisclosureDespiteSocialConsent(ProtectionAction action)
    {
        var repository = new SyntheticRepository { Records = [new NdaRecord { CustomerId = 23, SurvivalKind = NdaSurvivalKind.Indefinite }] };
        var service = new DocumentProtectionService(new SyntheticAuthority(DocumentActorKind.Employee), repository, new NdaOptions { Enabled = true }, TimeProvider.System);
        var result = await service.EvaluateAsync(Actor(), 23, new(DocumentResourceKind.Replacement, 17, action, true), default);
        Assert.False(result.Allowed);
        Assert.True(result.Protected);
    }

    [Fact]
    public async Task ScopedReadRemainsAllowedUnderProtection()
    {
        var repository = new SyntheticRepository { Records = [new NdaRecord { CustomerId = 23, SurvivalKind = NdaSurvivalKind.Unknown }] };
        var service = new DocumentProtectionService(new SyntheticAuthority(DocumentActorKind.Employee), repository, new NdaOptions { Enabled = true }, TimeProvider.System);
        Assert.True((await service.EvaluateAsync(Actor(), 23, new(DocumentResourceKind.Order, 17, ProtectionAction.Read), default)).Allowed);
    }

    [Fact]
    public async Task DisabledSchedulerDoesNotConsultAuthorityOrPersistence()
    {
        var service = new NdaReminderScheduler(new SyntheticRepository(), new SyntheticAuthority(DocumentActorKind.Employee), new NdaOptions(), TimeProvider.System);
        Assert.Equal(0, await service.QueueDueAsync(default));
    }

    [Fact]
    public void BangkokCalendarLeadDateUsesExplicitTimezone()
    {
        var expiry = new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero),
            NdaReminderScheduler.DueAtUtc(expiry, 1, TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok")));
    }

    private static DocumentActor Actor() => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "employee")], "test")));
    private static NdaVerificationRequest Request() => new(Guid.NewGuid(), 1, "Company A", "Company B", DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddMonths(1), null, NdaSurvivalKind.Unknown, null, "responsible", [new(DocumentResourceKind.Order, 17)], "Reviewed external signature");
}

internal sealed class SyntheticAuthority(DocumentActorKind kind) : INdaAuthority
{
    public DocumentAuthorityOutcome StaffOutcome { get; set; } = DocumentAuthorityOutcome.Allowed;
    public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) => Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, kind, "employee"));
    public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) => Task.FromResult(StaffOutcome);
    public Task<CanonicalNdaResources> ResolveCoverageAsync(DocumentActor actor, int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) => Task.FromResult(new CanonicalNdaResources(DocumentAuthorityOutcome.Allowed, customerId, resources, versionId, false));
    public Task<CanonicalNdaResources> ResolveProtectionCoverageAsync(DocumentActor actor, int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) => ResolveCoverageAsync(actor, customerId, resources, versionId, token);
    public Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token) => Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, kind, "employee"));
}
internal sealed class SyntheticEvidence : ICustomerDocumentContentEvidence
{
    public Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
}
internal sealed class SyntheticRepository : INdaRepository
{
    public IReadOnlyList<NdaRecord> Records { get; set; } = [];
    public Task<NdaAgreementReadback?> ReadAgreementAsync(int customerId, Guid documentId, Guid? versionId, CancellationToken token) => Task.FromResult<NdaAgreementReadback?>(null);
    public Task<NdaVerificationTarget?> ReadVerificationTargetAsync(int customerId, Guid documentId, Guid versionId, CancellationToken token) => Task.FromResult<NdaVerificationTarget?>(new(documentId, versionId, customerId, DocumentKind.Nda, new string('a', 64), 1));
    public Task<NdaRecord> AppendVerificationAsync(NdaRecord record, IReadOnlyList<NdaCoverageRequest> coverage, long expectedRevision, string reason, CancellationToken token) => Task.FromResult(record);
    public Task<IReadOnlyList<NdaRecord>> ReadCoveredAgreementsAsync(int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) => Task.FromResult(Records);
    public Task<IReadOnlyList<NdaRecord>> ReadCurrentAgreementsAsync(CancellationToken token) => Task.FromResult(Records);
    public Task<int> QueueReminderAsync(NdaRecord record, int leadDays, DateTimeOffset dueAtUtc, DateTimeOffset now, CancellationToken token) => Task.FromResult(1);
    public Task<IReadOnlyList<InternalNdaReminderSummary>> ReadWorklistAsync(string responsibleSubject, int limit, CancellationToken token,
        DateTimeOffset? dueFromUtc = null, DateTimeOffset? dueThroughUtc = null, InternalNdaReminderState? state = null) => Task.FromResult<IReadOnlyList<InternalNdaReminderSummary>>([]);
}
