using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

[Collection(CustomerDocumentPostgreSqlCollection.Name)]
public sealed class NdaReminderPostgreSqlTests(CustomerDocumentPostgreSqlFixture fixture)
{
    [Fact]
    public async Task FirstScheduleHasExactSystemAuditAndRetryDoesNotDuplicateIt()
    {
        var record = await Verify();
        // PostgreSQL timestamp precision is one microsecond; supply an exactly representable input.
        var instant = record.VerifiedAtUtc.AddHours(1);
        var now = new DateTimeOffset(instant.Ticks / 10 * 10, TimeSpan.Zero);
        var due = now.AddMinutes(-1);
        await using (var db = fixture.CreateContext())
        {
            var repository = new NdaRepository(db);
            Assert.Equal(1, await repository.QueueReminderAsync(record, 7, due, now, default));
            Assert.Equal(0, await repository.QueueReminderAsync(record, 7, due, now, default));
        }
        await using var read = fixture.CreateContext();
        var audit = Assert.Single(await read.Audits.Where(x => x.DocumentId == record.DocumentId && x.Action == "NdaReminderQueued").ToListAsync());
        Assert.Equal(record.VersionId, audit.VersionId);
        Assert.Equal(record.VerificationRevision, audit.Revision);
        Assert.Equal("system:nda-reminder-scheduler", audit.ActorSubject);
        Assert.Equal(now, audit.AtUtc);
    }
    [Fact]
    public async Task ConcurrentReminderQueueCommitsExactlyOneTaskPerAgreementEpoch()
    {
        var record = await Verify();
        var due = new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero);
        async Task<int> Queue()
        {
            await using var db = fixture.CreateContext();
            return await new NdaRepository(db).QueueReminderAsync(record, 1, due, due.AddHours(1), default);
        }
        var counts = await Task.WhenAll(Queue(), Queue());
        Assert.Equal(1, counts.Sum());
        await using var read = fixture.CreateContext();
        Assert.Single(await read.Set<InternalNdaReminder>().Where(x => x.NdaId == record.Id).ToListAsync());
    }

    [Fact]
    public async Task StaleRevisionCannotAppendVerificationOrCoverage()
    {
        var identity = await fixture.SeedAsync();
        var record = Record(identity.Document, identity.Version);
        await using var db = fixture.CreateContext();
        var repository = new NdaRepository(db);
        await repository.AppendVerificationAsync(record, [new(DocumentResourceKind.Order, 81)], 1, "Synthetic review", default);
        var stale = Record(identity.Document, identity.Version);
        await Assert.ThrowsAsync<NdaRevisionConflictException>(() => repository.AppendVerificationAsync(stale, [new(DocumentResourceKind.Order, 81)], 1, "Synthetic stale review", default));
        await using var read = fixture.CreateContext();
        Assert.Single(await read.Set<NdaRecord>().Where(x => x.DocumentId == identity.Document).ToListAsync());
    }

    [Fact]
    public async Task RenewalCancelsFutureTaskAndRejectsStaleEpochQueue()
    {
        var record = await Verify();
        var due = DateTimeOffset.UtcNow.AddDays(10);
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await new NdaRepository(db).QueueReminderAsync(record, 7, due, DateTimeOffset.UtcNow, default));
        await using (var db = fixture.CreateContext())
        {
            var renewed = Record(record.DocumentId, record.VersionId);
            await new NdaRepository(db).AppendVerificationAsync(renewed, [new(DocumentResourceKind.Order, 81)], 2, "Synthetic renewal review", default);
        }
        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await new NdaRepository(db).QueueReminderAsync(record, 1, due, due, default));
        await using var read = fixture.CreateContext();
        Assert.Equal(InternalNdaReminderState.Cancelled, (await read.Set<InternalNdaReminder>().SingleAsync(x => x.NdaId == record.Id)).State);
        Assert.Equal(2, await read.Set<NdaRecord>().CountAsync(x => x.DocumentId == record.DocumentId));
        Assert.Equal(2, await read.Set<NdaCoverage>().CountAsync(x => x.CustomerId == 23 && (x.NdaId == record.Id || x.NdaId == read.Set<NdaRecord>().Where(n => n.SupersedesNdaId == record.Id).Select(n => n.Id).First())));
    }

    [Fact]
    public async Task AgreementAndCoverageRejectDirectSqlRewrites()
    {
        var record = await Verify();
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"NdaRecord\" SET \"PartyOne\" = 'changed' WHERE \"Id\" = {record.Id}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"NdaCoverage\" WHERE \"NdaId\" = {record.Id}"));
    }

    [Fact]
    public async Task ExistingDueTaskBecomesMissedWithoutDuplicateEpoch()
    {
        var record = await Verify();
        var due = DateTimeOffset.UtcNow.AddDays(-3);
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await new NdaRepository(db).QueueReminderAsync(record, 7, due, due.AddHours(1), default));
        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await new NdaRepository(db).QueueReminderAsync(record, 7, due, due.AddDays(2), default));
        await using var read = fixture.CreateContext();
        Assert.Equal(InternalNdaReminderState.Missed, (await read.Set<InternalNdaReminder>().SingleAsync(x => x.NdaId == record.Id)).State);
    }
    [Fact]
    public async Task VerifiedCoverageSetRejectsLaterInsertionWithoutNewLegalRevision()
    {
        var record = await Verify();
        await using var db = fixture.CreateContext();
        Assert.True((await db.Set<NdaRecord>().SingleAsync(x => x.Id == record.Id)).CoverageSealed);
        db.Set<NdaCoverage>().Add(new NdaCoverage { NdaId = record.Id, CustomerId = 23, Kind = DocumentResourceKind.Order, ResourceId = 99 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    private async Task<NdaRecord> Verify()
    {
        var identity = await fixture.SeedAsync();
        await using var db = fixture.CreateContext();
        return await new NdaRepository(db).AppendVerificationAsync(Record(identity.Document, identity.Version), [new(DocumentResourceKind.Order, 81)], 1, "Synthetic review", default);
    }
    private static NdaRecord Record(Guid document, Guid version) => new() { Id = Guid.NewGuid(), DocumentId = document, VersionId = version,
        CustomerId = 23, PartyOne = "Synthetic A", PartyTwo = "Synthetic B", EffectiveAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(1), SurvivalKind = NdaSurvivalKind.Unknown,
        ResponsibleEmployeeSubject = "synthetic-responsible", VerifiedBySubject = "synthetic-verifier", VerifiedAtUtc = DateTimeOffset.UtcNow };
}
