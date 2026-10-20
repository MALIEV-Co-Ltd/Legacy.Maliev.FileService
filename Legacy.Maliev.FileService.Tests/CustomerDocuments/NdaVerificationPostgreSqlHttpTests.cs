using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class NdaVerificationPostgreSqlHttpTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Fact]
    public async Task RegisteredActualHttpVerificationRetainsExactRevisionAndRejectsStaleReplay()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = Factory(DocumentActorKind.Employee);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Verify);
        var request = Request(identity.Version);
        using var response = await client.PostAsJsonAsync(Route(identity.Document), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<NdaVerificationReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(identity.Version, receipt.VersionId);
        Assert.Equal(2, receipt.VerificationRevision);
        using var stale = await client.PostAsJsonAsync(Route(identity.Document), request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var db = fixture.CreateContext();
        var record = await db.Set<NdaRecord>().SingleAsync(x => x.DocumentId == identity.Document);
        Assert.Equal("employee", record.VerifiedBySubject);
        Assert.Equal(identity.Version, record.VersionId);
        Assert.Equal(2, record.VerificationRevision);
        Assert.Single(await db.Audits.Where(x => x.DocumentId == identity.Document && x.Action == "NdaVerified").ToListAsync());
    }

    [Fact]
    public async Task RegisteredActualHttpMemberCannotVerify()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = Factory(DocumentActorKind.Member);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Verify);
        using var response = await client.PostAsJsonAsync(Route(identity.Document), Request(identity.Version));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Empty(await db.Set<NdaRecord>().Where(x => x.DocumentId == identity.Document).ToListAsync());
    }

    [Fact]
    public async Task RegisteredActualHttpValidForeignVersionCannotVerify()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = Factory(DocumentActorKind.Employee);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Verify);
        using var response = await client.PostAsJsonAsync($"customers/24/documents/{identity.Document}/nda/verification", Request(identity.Version));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredActualHttpUnavailableResponsibleEmployeeFailsClosed()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = Factory(DocumentActorKind.Employee, DocumentAuthorityOutcome.Unavailable);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Verify);
        using var response = await client.PostAsJsonAsync(Route(identity.Document), Request(identity.Version));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReloadedExpiredAgreementRetainsServerComputedProtection()
    {
        var identity = await fixture.SeedAsync();
        await using var factory = Factory(DocumentActorKind.Employee);
        using var client = factory.AuthenticatedClient(CustomerDocumentPermissions.Verify);
        using var verified = await client.PostAsJsonAsync(Route(identity.Document), Request(identity.Version));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        using var reader = factory.AuthenticatedClient(CustomerDocumentPermissions.Read);
        using var response = await reader.GetAsync($"customers/23/documents/{identity.Document}/nda");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<NdaAgreementSummary>();
        Assert.NotNull(summary);
        Assert.Equal(AgreementCalendarStatus.Expired, summary.AgreementStatus);
        Assert.Equal(ConfidentialityObligationStatus.ReviewRequired, summary.ObligationStatus);
        Assert.Equal(identity.Version, summary.VersionId);
    }
    private NdaHttpFactory Factory(DocumentActorKind kind, DocumentAuthorityOutcome staff = DocumentAuthorityOutcome.Allowed) => new(services =>
    {
        services.AddCustomerDocuments(fixture.Connection, enabled: true);
        services.AddCustomerDocumentNda(new NdaOptions { Enabled = true });
        services.AddScoped<INdaAuthority>(_ => new SyntheticAuthority(kind) { StaffOutcome = staff });
        services.AddScoped<ICustomerDocumentContentEvidence, SyntheticEvidence>();
    });
    private static string Route(Guid document) => $"customers/23/documents/{document}/nda/verification";
    private static NdaVerificationRequest Request(Guid version) => new(version, 1, "Synthetic A", "Synthetic B", DateTimeOffset.UtcNow.AddYears(-1),
        DateTimeOffset.UtcNow.AddDays(-1), null, NdaSurvivalKind.Unknown, null, "synthetic-responsible", [new(DocumentResourceKind.Order, 81)], "Reviewed synthetic external signature");
}
