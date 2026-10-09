using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class NdaReminderFilterPostgreSqlHttpTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Fact]
    public async Task ExactUtcIntervalAndStateFilterBeforeLimitRetainOnlyCurrentRecipient()
    {
        var subject = $"filter-{Guid.NewGuid():N}";
        var due = new DateTimeOffset(2026, 10, 8, 17, 0, 0, TimeSpan.Zero);
        var record = await Verify(subject);
        var other = await Verify($"other-{Guid.NewGuid():N}");
        await using (var db = fixture.CreateContext())
        {
            var repository = new NdaRepository(db);
            await repository.QueueReminderAsync(record, 7, due.AddDays(-2), due, default);
            await repository.QueueReminderAsync(record, 1, due, due, default);
            await repository.QueueReminderAsync(record, 0, due.AddDays(2), due.AddDays(2), default);
            await repository.QueueReminderAsync(other, 1, due, due, default);
        }
        await using var factory = Factory(subject);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Read);
        using var response = await client.GetAsync("staff/nda-reminders?state=Due&dueFromUtc=2026-10-08T17%3A00%3A00Z&dueThroughUtc=2026-10-08T17%3A00%3A00Z&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<InternalNdaReminderSummary[]>())!);
        Assert.Equal(record.Id, item.NdaId);
        Assert.Equal(1, item.LeadDays);
        Assert.Equal(subject, item.ResponsibleEmployeeSubject);
        Assert.Equal(due, item.DueAtUtc);
        Assert.Equal(InternalNdaReminderState.Due, item.State);
    }

    [Theory]
    [InlineData("state=0")]
    [InlineData("state=Unknown")]
    [InlineData("state=Due,Missed")]
    [InlineData("dueFromUtc=2026-10-09T00%3A00%3A00Z&dueThroughUtc=2026-10-08T00%3A00%3A00Z")]
    [InlineData("dueFromUtc=2026-01-01T00%3A00%3A00Z&dueThroughUtc=2028-01-01T00%3A00%3A00Z")]
    [InlineData("dueFromUtc=2026-10-08T00%3A00%3A00%2B07%3A00")]
    [InlineData("dueThroughUtc=2026-10-08T00%3A00%3A00%2B07%3A00")]
    public async Task InvalidDateOrUnnamedStateIsRejected(string query)
    {
        await using var factory = Factory($"filter-{Guid.NewGuid():N}");
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Read);
        using var response = await client.GetAsync($"staff/nda-reminders?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FiltersDoNotPermitMemberWorklistAccess()
    {
        await using var factory = Factory($"filter-{Guid.NewGuid():N}", DocumentActorKind.Member);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Read);
        using var response = await client.GetAsync("staff/nda-reminders?state=Due");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private NdaHttpFactory Factory(string subject, DocumentActorKind kind = DocumentActorKind.Employee) => new(services =>
    {
        services.AddCustomerDocuments(fixture.Connection, enabled: true);
        services.AddCustomerDocumentNda(new NdaOptions { Enabled = true });
        services.AddScoped<INdaAuthority>(_ => new WorklistAuthority(subject, kind));
    });

    private async Task<NdaRecord> Verify(string subject)
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        return await new NdaRepository(db).AppendVerificationAsync(new NdaRecord
        {
            Id = Guid.NewGuid(),
            DocumentId = identity.Document,
            VersionId = identity.Version,
            CustomerId = 23,
            PartyOne = "Synthetic A",
            PartyTwo = "Synthetic B",
            EffectiveAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(1),
            SurvivalKind = NdaSurvivalKind.Unknown,
            ResponsibleEmployeeSubject = subject,
            VerifiedBySubject = "synthetic-verifier",
            VerifiedAtUtc = DateTimeOffset.UtcNow
        }, [new(DocumentResourceKind.Order, 81)], 1, "Synthetic filter review", default);
    }

    private sealed class WorklistAuthority(string subject, DocumentActorKind kind) : INdaAuthority
    {
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) =>
            Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, kind, subject));
        public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string employee, CancellationToken token) => Task.FromResult(DocumentAuthorityOutcome.Allowed);
        public Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token) =>
            Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, kind, subject));
        public Task<CanonicalNdaResources> ResolveCoverageAsync(DocumentActor actor, int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) =>
            Task.FromResult(new CanonicalNdaResources(DocumentAuthorityOutcome.Unavailable, customerId, []));
        public Task<CanonicalNdaResources> ResolveProtectionCoverageAsync(DocumentActor actor, int customerId, IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token) =>
            Task.FromResult(new CanonicalNdaResources(DocumentAuthorityOutcome.Unavailable, customerId, []));
    }
}
