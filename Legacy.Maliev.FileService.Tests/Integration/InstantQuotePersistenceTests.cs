using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
public sealed class InstantQuotePersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T12:00:00Z");

    [Fact]
    public async Task Session_CreateLoadAndVerify_PersistsOnlyHashedCapability()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();

        await repository.CreateSessionAsync(session, CancellationToken.None);
        context.ChangeTracker.Clear();

        var loaded = await repository.GetSessionAsync(session.Id, CancellationToken.None);
        var verified = await repository.VerifySessionAsync(
            session.Id,
            Hash("session-token"),
            session.OwnerSubject,
            session.IsAuthenticated,
            Now,
            CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(Hash("session-token"), loaded.TokenHash);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes("session-token"), loaded.TokenHash);
        Assert.NotNull(verified);
        Assert.Null(await repository.VerifySessionAsync(
            session.Id,
            Hash("wrong-token"),
            session.OwnerSubject,
            session.IsAuthenticated,
            Now,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReserveUpload_SameKeyClassifiesReplayConflictAndInProgress()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        var upload = CreateUpload(session.Id, "fingerprint-a");

        var acquired = await repository.ReserveUploadAsync(upload, CancellationToken.None);
        context.ChangeTracker.Clear();
        var inProgress = await repository.ReserveUploadAsync(CreateUpload(session.Id, "fingerprint-a"), CancellationToken.None);
        acquired.Record.State = InstantQuoteWorkflowState.Clean;
        acquired.Record.ActualSha256 = new string('b', 64);
        acquired.Record.ActualSizeBytes = 42;
        acquired.Record.GcsGeneration = 7;
        acquired.Record.ModifiedAt = Now.AddMinutes(1);
        await repository.SaveUploadAsync(acquired.Record, CancellationToken.None);
        context.ChangeTracker.Clear();
        var replay = await repository.ReserveUploadAsync(CreateUpload(session.Id, "fingerprint-a"), CancellationToken.None);
        context.ChangeTracker.Clear();
        var conflict = await repository.ReserveUploadAsync(CreateUpload(session.Id, "fingerprint-b"), CancellationToken.None);

        Assert.Equal(InstantQuoteReservationStatus.Acquired, acquired.Status);
        Assert.Equal(InstantQuoteReservationStatus.InProgress, inProgress.Status);
        Assert.Equal(InstantQuoteReservationStatus.Replay, replay.Status);
        Assert.Equal(upload.Id, replay.Record.Id);
        Assert.Equal(InstantQuoteReservationStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task ReserveUpload_ConcurrentPostgreSqlReservations_HaveOneOwner()
    {
        await using (var setupContext = await CreateMigratedContextAsync())
        {
            await new InstantQuoteFileRepository(setupContext).CreateSessionAsync(CreateSession(), CancellationToken.None);
        }

        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var sessionId = await firstContext.InstantQuoteUploadSessions.Select(value => value.Id).SingleAsync();
        var firstRepository = new InstantQuoteFileRepository(firstContext);
        var secondRepository = new InstantQuoteFileRepository(secondContext);

        var results = await Task.WhenAll(
            firstRepository.ReserveUploadAsync(CreateUpload(sessionId, "same-request"), CancellationToken.None),
            secondRepository.ReserveUploadAsync(CreateUpload(sessionId, "same-request"), CancellationToken.None));

        Assert.Single(results, result => result.Status == InstantQuoteReservationStatus.Acquired);
        Assert.Single(results, result => result.Status == InstantQuoteReservationStatus.InProgress);
        Assert.Equal(results[0].Record.Id, results[1].Record.Id);
    }

    [Fact]
    public async Task ReserveFinalization_DeterministicSelectionAndUnknownState_AreDurable()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        var first = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var second = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var finalization = new InstantQuoteFinalization(
            Guid.NewGuid(), session.Id, Hash("finalize-key"), new string('c', 64), Guid.NewGuid(),
            [second, first], InstantQuoteWorkflowState.Pending, Now, Now);

        var acquired = await repository.ReserveFinalizationAsync(finalization, CancellationToken.None);
        acquired.Record.State = InstantQuoteWorkflowState.Unknown;
        acquired.Record.ModifiedAt = Now.AddMinutes(1);
        await repository.SaveFinalizationAsync(acquired.Record, CancellationToken.None);
        context.ChangeTracker.Clear();
        var unknown = await repository.ReserveFinalizationAsync(
            new InstantQuoteFinalization(Guid.NewGuid(), session.Id, Hash("finalize-key"), new string('c', 64),
                finalization.QuotationRequestId, [first, second], InstantQuoteWorkflowState.Pending, Now, Now),
            CancellationToken.None);

        Assert.Equal([first, second], acquired.Record.SelectedFileIds);
        Assert.Equal(InstantQuoteReservationStatus.Unknown, unknown.Status);
        Assert.Equal(acquired.Record.Id, unknown.Record.Id);
    }

    [Fact]
    public async Task SessionDelete_CascadesWorkflowRows_AndMappingsUseXmin()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        await repository.ReserveUploadAsync(CreateUpload(session.Id, "fingerprint-a"), CancellationToken.None);
        await repository.ReserveFinalizationAsync(
            new InstantQuoteFinalization(Guid.NewGuid(), session.Id, Hash("finalize-key"), new string('c', 64),
                Guid.NewGuid(), [], InstantQuoteWorkflowState.Pending, Now, Now),
            CancellationToken.None);

        context.InstantQuoteUploadSessions.Remove(session);
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.Empty(await context.InstantQuoteUploadFiles.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await context.InstantQuoteFinalizations.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.True(context.Model.FindEntityType(typeof(InstantQuoteUploadSession))!.FindProperty("xmin")!.IsConcurrencyToken);
        Assert.True(context.Model.FindEntityType(typeof(InstantQuoteUploadFile))!.FindProperty("xmin")!.IsConcurrencyToken);
        Assert.True(context.Model.FindEntityType(typeof(InstantQuoteFinalization))!.FindProperty("xmin")!.IsConcurrencyToken);
    }

    [Fact]
    public async Task SaveUpload_StaleXmin_RejectsLostUpdate()
    {
        Guid uploadId;
        await using (var setupContext = await CreateMigratedContextAsync())
        {
            var repository = new InstantQuoteFileRepository(setupContext);
            var session = CreateSession();
            await repository.CreateSessionAsync(session, CancellationToken.None);
            var acquired = await repository.ReserveUploadAsync(CreateUpload(session.Id, "concurrency"), CancellationToken.None);
            uploadId = acquired.Record.Id;
        }

        await using var firstContext = fixture.CreateContext();
        await using var staleContext = fixture.CreateContext();
        var first = await firstContext.InstantQuoteUploadFiles.SingleAsync(value => value.Id == uploadId);
        var stale = await staleContext.InstantQuoteUploadFiles.SingleAsync(value => value.Id == uploadId);
        first.State = InstantQuoteWorkflowState.Uploaded;
        stale.State = InstantQuoteWorkflowState.Failed;

        await new InstantQuoteFileRepository(firstContext).SaveUploadAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            new InstantQuoteFileRepository(staleContext).SaveUploadAsync(stale, CancellationToken.None));
    }

    private async Task<FileDbContext> CreateMigratedContextAsync()
    {
        var context = fixture.CreateContext();
        await context.Database.MigrateAsync(CancellationToken.None);
        await context.InstantQuoteFinalizations.ExecuteDeleteAsync(CancellationToken.None);
        await context.InstantQuoteUploadFiles.ExecuteDeleteAsync(CancellationToken.None);
        await context.InstantQuoteUploadSessions.ExecuteDeleteAsync(CancellationToken.None);
        return context;
    }

    private static InstantQuoteUploadSession CreateSession() => new(
        Guid.NewGuid(), "https://issuer.example|user-42", true, Hash("session-token"), Now.AddHours(1), Now);

    private static InstantQuoteUploadFile CreateUpload(Guid sessionId, string fingerprint) => new(
        Guid.NewGuid(), sessionId, Hash("upload-key"), Fingerprint(fingerprint), "part.stl", ".stl", "model/stl",
        new string('a', 64), null, null, null, $"instant-quote/{sessionId:N}/{Guid.NewGuid():N}", null,
        InstantQuoteWorkflowState.Pending, Now, Now);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Fingerprint(string value) => Convert.ToHexString(Hash(value)).ToLowerInvariant();
}
