using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Moq;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class ProtectedDocumentPersistenceTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Fact]
    public async Task ReservedReplacementCannotFinalizeAfterRevisionChanges()
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        var document = new CustomerDocument { Id = Guid.NewGuid(), CustomerId = 23, Kind = DocumentKind.Nda, Visibility = DocumentVisibility.Customer, Title = "Synthetic" };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        var store = Store(db);
        var request = new DocumentUploadRequest(document.Id, DocumentKind.Nda, document.Title, document.Visibility, [], 1);
        var reservation = await store.ReserveAsync(23, "synthetic", Guid.NewGuid().ToString(), new string('b', 64), request, default);
        document.Revision = 2;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DocumentConflictException>(() => store.FinalizeAsync(new(new ClaimsPrincipal()), reservation, request, Content(reservation), "synthetic", default));
        Assert.False(await db.Versions.AnyAsync(x => x.Id == reservation.VersionId));
    }
    [Fact]
    public async Task ArchiveRetainsOriginalAndRefusesLegalHold()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        var document = await db.Documents.SingleAsync(x => x.Id == identity.Document);
        db.Set<CustomerDocumentLegalHold>().Add(new() { DocumentId = document.Id, CustomerId = 23, Reason = "Synthetic hold" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DocumentConflictException>(() => Store(db).ArchiveAsync(23, document.Id, document.Revision, "Synthetic retention", "synthetic", DocumentActorKind.Employee, default));
        Assert.Null((await db.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id)).ArchivedAtUtc);
        Assert.True(await db.Versions.AnyAsync(x => x.Id == identity.Version));
    }
    [Fact]
    public async Task HoldCommittedWhileArchiveWaitsForAdvisoryLockPreventsArchive()
    {
        var identity = await fixture.SeedAsync();
        await using var blocker = fixture.CreateContext();
        await using var observer = fixture.CreateContext();
        await using var archive = fixture.CreateContext();
        await using var hold = fixture.CreateContext();
        var document = await observer.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        await using var transaction = await blocker.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await blocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(23)");
        await archive.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)archive.Database.GetDbConnection()).ProcessID;
        var attempt = Store(archive).ArchiveAsync(23, document.Id, document.Revision, "Synthetic retention", "synthetic", DocumentActorKind.Employee, default);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var waiting = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            waiting = await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event = 'advisory') AS \"Value\"").SingleAsync();
            if (waiting) break;
            await Task.Delay(20);
        }
        if (!waiting)
        {
            await transaction.RollbackAsync();
            await attempt;
            Assert.Fail("Archive did not reach the controlled advisory-lock wait.");
        }
        hold.Set<CustomerDocumentLegalHold>().Add(new() { DocumentId = document.Id, CustomerId = 23, Reason = "Synthetic concurrent hold" });
        await hold.SaveChangesAsync();
        await transaction.CommitAsync();
        await Assert.ThrowsAsync<DocumentConflictException>(() => attempt);
        Assert.Null((await observer.Documents.AsNoTracking().SingleAsync(x => x.Id == document.Id)).ArchivedAtUtc);
        Assert.True(await observer.Versions.AnyAsync(x => x.Id == identity.Version));
    }
    [Fact]
    public async Task FinalizationWaitingBehindCommittedRevisionReturnsConflict()
    {
        var identity = await fixture.SeedAsync();
        await using var reservations = fixture.CreateContext();
        var document = await reservations.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        var request = new DocumentUploadRequest(document.Id, document.Kind, document.Title, document.Visibility, [], document.Revision);
        var first = await Store(reservations).ReserveAsync(23, "synthetic", Guid.NewGuid().ToString(), new string('b', 64), request, default);
        var second = await Store(reservations).ReserveAsync(23, "synthetic", Guid.NewGuid().ToString(), new string('b', 64), request, default);
        await using var winner = fixture.CreateContext(); await using var loser = fixture.CreateContext(); await using var observer = fixture.CreateContext();
        await loser.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)loser.Database.GetDbConnection()).ProcessID;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerTask = Store(winner, async () => { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }).FinalizeAsync(new(new ClaimsPrincipal()), first, request, Content(first), "synthetic", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loserTask = Store(loser).FinalizeAsync(new(new ClaimsPrincipal()), second, request, Content(second), "synthetic", default);
        var deadline = System.Diagnostics.Stopwatch.StartNew(); var waiting = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            waiting = await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid={pid} AND wait_event='advisory') AS \"Value\"").SingleAsync();
            if (waiting) break;
            await Task.Delay(20);
        }
        release.SetResult();
        await winnerTask;
        Assert.True(waiting, "Losing finalization did not reach the controlled advisory-lock wait.");
        await Assert.ThrowsAsync<DocumentConflictException>(() => loserTask);
        Assert.False(await observer.Versions.AnyAsync(x => x.Id == second.VersionId));
        Assert.True(await observer.Versions.AnyAsync(x => x.Id == first.VersionId && x.AssociationsSealed));
    }
    [Fact]
    public async Task DifferentContentCannotReplayReservedKey()
    {
        await using var db = fixture.CreateContext();
        await db.Database.MigrateAsync();
        var store = Store(db);
        var request = new DocumentUploadRequest(null, DocumentKind.Nda, "Synthetic", DocumentVisibility.Customer, []);
        var key = Guid.NewGuid().ToString();
        await store.ReserveAsync(23, "synthetic", key, new string('b', 64), request, default);
        await Assert.ThrowsAsync<DocumentConflictException>(() => store.ReserveAsync(23, "synthetic", key, new string('c', 64), request, default));
        Assert.Single(await db.Set<ProtectedDocumentUploadCheckpoint>().Where(x => x.Subject == "synthetic" && x.KeyHash == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))).ToArrayAsync());
    }
    [Fact]
    public async Task NewReservationFollowsExistingImmutableVersionNumbers()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        var document = await db.Documents.SingleAsync(x => x.Id == identity.Document);
        var request = new DocumentUploadRequest(document.Id, document.Kind, document.Title, document.Visibility, [], document.Revision);
        var reservation = await Store(db).ReserveAsync(23, "synthetic", Guid.NewGuid().ToString(), new string('b', 64), request, default);
        Assert.Equal(2, reservation.VersionNumber);
    }
    [Fact]
    public async Task ConcurrentReservationsAllocateDistinctVersionNumbers()
    {
        var identity = await fixture.SeedAsync();
        await using var first = fixture.CreateContext(); await using var second = fixture.CreateContext();
        var document = await first.Documents.AsNoTracking().SingleAsync(x => x.Id == identity.Document);
        var request = new DocumentUploadRequest(document.Id, document.Kind, document.Title, document.Visibility, [], document.Revision);
        var reservations = await Task.WhenAll(Store(first).ReserveAsync(23, "synthetic", Guid.NewGuid().ToString(), new string('b', 64), request, default), Store(second).ReserveAsync(23, "synthetic", Guid.NewGuid().ToString(), new string('b', 64), request, default));
        Assert.Equal(new[] { 2, 3 }, reservations.Select(x => x.VersionNumber).Order().ToArray());
    }
    private static ProtectedDocumentStore Store(CustomerDocumentDbContext db, Func<Task>? beforeFinalize = null)
    {
        var authority = new Mock<ICustomerDocumentAuthority>();
        authority.Setup(x => x.AuthorizeAsync(It.IsAny<DocumentActor>(), 23, CustomerDocumentPermissions.Write, It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "synthetic"));
        var associations = new Mock<ICustomerDocumentAssociationValidator>();
        associations.Setup(x => x.ValidateAsync(It.IsAny<DocumentActor>(), 23, It.IsAny<IReadOnlyList<DocumentAssociation>>(), It.IsAny<CancellationToken>())).ReturnsAsync(DocumentAuthorityOutcome.Allowed);
        var registry = new CustomerDocumentRegistry(db, authority.Object, associations.Object, new Mock<ICustomerDocumentContentEvidence>().Object, new() { Enabled = true }, TimeProvider.System);
        if (beforeFinalize is null) return new(db, new Mock<IStorageMoveJournal>().Object, registry, TimeProvider.System);
        var gate = new Mock<ICustomerDocumentRegistry>();
        gate.Setup(x => x.FinalizeVersionAsync(It.IsAny<DocumentActor>(), It.IsAny<CustomerDocumentVersion>(), It.IsAny<IReadOnlyList<DocumentAssociation>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DocumentActor actor, CustomerDocumentVersion version, IReadOnlyList<DocumentAssociation> links, CancellationToken token) => { await beforeFinalize(); await registry.FinalizeVersionAsync(actor, version, links, token); });
        return new(db, new Mock<IStorageMoveJournal>().Object, gate.Object, TimeProvider.System);
    }
    private static DocumentStoredContent Content(DocumentUploadReservation reservation) => new("synthetic", $"customer-documents/23/{reservation.DocumentId:N}/{reservation.VersionId:N}/original", 73, 72, reservation.OperationId, 20, new string('b', 64), "application/pdf", "synthetic.pdf");
}
