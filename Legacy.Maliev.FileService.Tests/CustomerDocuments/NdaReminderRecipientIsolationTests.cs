using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class NdaReminderRecipientIsolationTests
{
    [Fact]
    public async Task DeniedFirstRecipientDoesNotStarveActiveAgreementAndEmitsSanitizedEscalation()
    {
        var repository = new QueueRepository();
        var log = new CapturedLog();
        using var services = new ServiceCollection().AddSingleton<INdaRepository>(repository)
            .AddSingleton<INdaAuthority>(new RecipientAuthority(DocumentAuthorityOutcome.Denied))
            .AddSingleton(new NdaOptions { Enabled = true, SchedulerEnabled = true, LeadDays = [0] })
            .AddSingleton(TimeProvider.System).AddSingleton<ILogger<NdaReminderScheduler>>(log)
            .AddSingleton<NdaReminderScheduler>().BuildServiceProvider();
        Assert.Equal(1, await services.GetRequiredService<NdaReminderScheduler>().QueueDueAsync(default));
        Assert.Equal(["active-secret-subject"], repository.QueuedSubjects);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("review", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inactive-secret-subject", warning);
        Assert.DoesNotContain("active-secret-subject", warning);
    }

    [Fact]
    public async Task CommonAuthorityUnavailableStillFailsClosedBeforeLaterQueue()
    {
        var repository = new QueueRepository();
        var scheduler = new NdaReminderScheduler(repository, new RecipientAuthority(DocumentAuthorityOutcome.Unavailable),
            new NdaOptions { Enabled = true, SchedulerEnabled = true, LeadDays = [0] }, TimeProvider.System);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => scheduler.QueueDueAsync(default));
        Assert.Empty(repository.QueuedSubjects);
    }

    private sealed class RecipientAuthority(DocumentAuthorityOutcome inactiveOutcome) : INdaAuthority
    {
        public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) =>
            Task.FromResult(subject == "inactive-secret-subject" ? inactiveOutcome : DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) => throw new NotSupportedException();
        public Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token) => throw new NotSupportedException();
        public Task<CanonicalNdaResources> ResolveCoverageAsync(DocumentActor actor, int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) => throw new NotSupportedException();
        public Task<CanonicalNdaResources> ResolveProtectionCoverageAsync(DocumentActor actor, int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class QueueRepository : INdaRepository
    {
        public List<string> QueuedSubjects { get; } = [];
        public Task<IReadOnlyList<NdaRecord>> ReadCurrentAgreementsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<NdaRecord>>([
            new() { ResponsibleEmployeeSubject = "inactive-secret-subject", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new() { ResponsibleEmployeeSubject = "active-secret-subject", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }]);
        public Task<int> QueueReminderAsync(NdaRecord record, int leadDays, DateTimeOffset dueAtUtc, DateTimeOffset now, CancellationToken token)
        { QueuedSubjects.Add(record.ResponsibleEmployeeSubject); return Task.FromResult(1); }
        public Task<NdaAgreementReadback?> ReadAgreementAsync(int customerId, Guid documentId, Guid? versionId, CancellationToken token) => throw new NotSupportedException();
        public Task<NdaVerificationTarget?> ReadVerificationTargetAsync(int customerId, Guid documentId, Guid versionId, CancellationToken token) => throw new NotSupportedException();
        public Task<NdaRecord> AppendVerificationAsync(NdaRecord record, IReadOnlyList<NdaCoverageRequest> coverage, long expectedRevision, string reason, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<NdaRecord>> ReadCoveredAgreementsAsync(int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<InternalNdaReminderSummary>> ReadWorklistAsync(string responsibleSubject, int limit, CancellationToken token,
            DateTimeOffset? dueFromUtc = null, DateTimeOffset? dueThroughUtc = null, InternalNdaReminderState? state = null) => throw new NotSupportedException();
    }

    private sealed class CapturedLog : ILogger<NdaReminderScheduler>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Warnings.Add(formatter(state, error)); }
    }
}
