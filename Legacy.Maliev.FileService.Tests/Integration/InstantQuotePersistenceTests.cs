using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

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
        await repository.SaveUploadAsync(acquired.Record, acquired.Version, CancellationToken.None);
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
        await repository.SaveFinalizationAsync(acquired.Record, acquired.Version, CancellationToken.None);
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
        var upload = context.Model.FindEntityType(typeof(InstantQuoteUploadFile))!;
        Assert.False(upload.FindProperty("TemporaryBucket")!.IsNullable);
        Assert.True(upload.FindProperty("FinalBucket")!.IsNullable);
        Assert.True(upload.FindProperty("FinalizedQuotationRequestId")!.IsNullable);
    }

    [Fact]
    public async Task SaveUpload_DetachedReplayWithStaleXmin_RejectsLostUpdate()
    {
        Guid sessionId;
        await using (var setupContext = await CreateMigratedContextAsync())
        {
            var repository = new InstantQuoteFileRepository(setupContext);
            var session = CreateSession();
            await repository.CreateSessionAsync(session, CancellationToken.None);
            var acquired = await repository.ReserveUploadAsync(CreateUpload(session.Id, "concurrency"), CancellationToken.None);
            acquired.Record.State = InstantQuoteWorkflowState.Clean;
            await repository.SaveUploadAsync(acquired.Record, acquired.Version, CancellationToken.None);
            sessionId = session.Id;
        }

        await using var staleContext = fixture.CreateContext();
        var staleRepository = new InstantQuoteFileRepository(staleContext);
        var staleReplay = await staleRepository.ReserveUploadAsync(CreateUpload(sessionId, "concurrency"), CancellationToken.None);
        Assert.Equal(InstantQuoteReservationStatus.Replay, staleReplay.Status);
        Assert.Equal(EntityState.Detached, staleContext.Entry(staleReplay.Record).State);

        await using (var advancingContext = fixture.CreateContext())
        {
            var advancing = await advancingContext.InstantQuoteUploadFiles.SingleAsync(value => value.Id == staleReplay.Record.Id);
            advancing.State = InstantQuoteWorkflowState.Finalized;
            await advancingContext.SaveChangesAsync(CancellationToken.None);
        }

        staleReplay.Record.State = InstantQuoteWorkflowState.Failed;

        await Assert.ThrowsAsync<InstantQuoteConcurrencyException>(() =>
            staleRepository.SaveUploadAsync(staleReplay.Record, staleReplay.Version, CancellationToken.None));
        Assert.Equal(EntityState.Detached, staleContext.Entry(staleReplay.Record).State);
    }

    [Fact]
    public async Task SaveFinalization_DetachedReplayWithStaleXmin_RejectsLostUpdate()
    {
        Guid sessionId;
        var quotationRequestId = Guid.NewGuid();
        await using (var setupContext = await CreateMigratedContextAsync())
        {
            var repository = new InstantQuoteFileRepository(setupContext);
            var session = CreateSession();
            sessionId = session.Id;
            await repository.CreateSessionAsync(session, CancellationToken.None);
            var acquired = await repository.ReserveFinalizationAsync(
                CreateFinalization(session.Id, quotationRequestId, "finalization-concurrency"), CancellationToken.None);
            acquired.Record.State = InstantQuoteWorkflowState.Finalized;
            await repository.SaveFinalizationAsync(acquired.Record, acquired.Version, CancellationToken.None);
        }

        await using var staleContext = fixture.CreateContext();
        var staleRepository = new InstantQuoteFileRepository(staleContext);
        var staleReplay = await staleRepository.ReserveFinalizationAsync(
            CreateFinalization(sessionId, quotationRequestId, "finalization-concurrency"), CancellationToken.None);
        Assert.Equal(InstantQuoteReservationStatus.Replay, staleReplay.Status);
        Assert.Equal(EntityState.Detached, staleContext.Entry(staleReplay.Record).State);

        await using (var advancingContext = fixture.CreateContext())
        {
            var advancing = await advancingContext.InstantQuoteFinalizations.SingleAsync(
                value => value.Id == staleReplay.Record.Id);
            advancing.State = InstantQuoteWorkflowState.Unknown;
            await advancingContext.SaveChangesAsync(CancellationToken.None);
        }

        staleReplay.Record.State = InstantQuoteWorkflowState.Failed;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            staleRepository.SaveFinalizationAsync(staleReplay.Record, staleReplay.Version, CancellationToken.None));
    }

    [Fact]
    public async Task GetSessionFiles_ExactIds_ReturnsOnlyOwnedRowsWithObservedXmin()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        var otherSession = new InstantQuoteUploadSession(
            Guid.NewGuid(), "https://issuer.example|other-user", true, Hash("other-token"), Now.AddHours(1), Now);
        await repository.CreateSessionAsync(session, CancellationToken.None);
        await repository.CreateSessionAsync(otherSession, CancellationToken.None);
        var owned = await repository.ReserveUploadAsync(CreateUpload(session.Id, "owned"), CancellationToken.None);
        var foreign = await repository.ReserveUploadAsync(CreateUpload(otherSession.Id, "foreign"), CancellationToken.None);

        var result = await repository.GetSessionFilesAsync(
            session.Id, [owned.Record.Id, foreign.Record.Id], CancellationToken.None);

        var stored = Assert.Single(result);
        Assert.Equal(owned.Record.Id, stored.Upload.Id);
        Assert.NotEqual(0U, stored.Version);
    }

    [Fact]
    public async Task SaveUpload_RemovedStateAndDurableBuckets_RoundTrips()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        var acquired = await repository.ReserveUploadAsync(CreateUpload(session.Id, "removed"), CancellationToken.None);
        acquired.Record.FinalBucket = "final-bucket";
        acquired.Record.FinalObjectName = "instant-quotation/final.stl";
        acquired.Record.FinalizedQuotationRequestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        acquired.Record.State = InstantQuoteWorkflowState.Removed;
        await repository.SaveUploadAsync(acquired.Record, acquired.Version, CancellationToken.None);

        var stored = Assert.Single(await repository.GetSessionFilesAsync(
            session.Id, [acquired.Record.Id], CancellationToken.None));
        Assert.Equal("private-bucket", stored.Upload.TemporaryBucket);
        Assert.Equal("final-bucket", stored.Upload.FinalBucket);
        Assert.Equal(acquired.Record.FinalizedQuotationRequestId, stored.Upload.FinalizedQuotationRequestId);
        Assert.Equal(InstantQuoteWorkflowState.Removed, stored.Upload.State);
    }

    [Fact]
    public async Task SaveUpload_ConcurrentQuotationAuthorities_XminAllowsOnlyOneWinner()
    {
        Guid sessionId;
        Guid fileId;
        await using (var setup = await CreateMigratedContextAsync())
        {
            var repository = new InstantQuoteFileRepository(setup);
            var session = CreateSession();
            sessionId = session.Id;
            await repository.CreateSessionAsync(session, CancellationToken.None);
            var acquired = await repository.ReserveUploadAsync(CreateUpload(session.Id, "quotation-race"), CancellationToken.None);
            acquired.Record.State = InstantQuoteWorkflowState.Clean;
            await repository.SaveUploadAsync(acquired.Record, acquired.Version, CancellationToken.None);
            fileId = acquired.Record.Id;
        }

        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var firstRepository = new InstantQuoteFileRepository(firstContext);
        var secondRepository = new InstantQuoteFileRepository(secondContext);
        var first = Assert.Single(await firstRepository.GetSessionFilesAsync(sessionId, [fileId], CancellationToken.None));
        var second = Assert.Single(await secondRepository.GetSessionFilesAsync(sessionId, [fileId], CancellationToken.None));
        var firstAuthority = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondAuthority = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        first.Upload.FinalizedQuotationRequestId = firstAuthority;
        first.Upload.State = InstantQuoteWorkflowState.Finalized;
        second.Upload.FinalizedQuotationRequestId = secondAuthority;
        second.Upload.State = InstantQuoteWorkflowState.Finalized;

        await firstRepository.SaveUploadAsync(first.Upload, first.Version, CancellationToken.None);
        await Assert.ThrowsAsync<InstantQuoteConcurrencyException>(() =>
            secondRepository.SaveUploadAsync(second.Upload, second.Version, CancellationToken.None));

        await using var verify = fixture.CreateContext();
        var stored = await verify.InstantQuoteUploadFiles.AsNoTracking().SingleAsync(value => value.Id == fileId);
        Assert.Equal(firstAuthority, stored.FinalizedQuotationRequestId);
    }

    [Fact]
    public async Task Finalize_SameAuthorityXminWinner_RepositoryContextRemainsUsableForFinalizationSave()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        var acquired = await repository.ReserveUploadAsync(
            CreateUpload(session.Id, "same-authority-race"), CancellationToken.None);
        acquired.Record.ActualSha256 = new string('a', 64);
        acquired.Record.ActualSizeBytes = 42;
        acquired.Record.GcsGeneration = 7;
        acquired.Record.State = InstantQuoteWorkflowState.Clean;
        await repository.SaveUploadAsync(acquired.Record, acquired.Version, CancellationToken.None);
        context.ChangeTracker.Clear();

        var requestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var destination = $"instant-quotation/{requestId:N}/{acquired.Record.Id:N}.stl";
        var storage = new SameAuthorityRaceStorage(
            destination,
            async () =>
            {
                await using var winnerContext = fixture.CreateContext();
                var winner = await winnerContext.InstantQuoteUploadFiles.SingleAsync(
                    value => value.Id == acquired.Record.Id);
                winner.FinalBucket = "private-bucket";
                winner.FinalObjectName = destination;
                winner.FinalizedQuotationRequestId = requestId;
                winner.State = InstantQuoteWorkflowState.Finalized;
                await winnerContext.SaveChangesAsync(CancellationToken.None);
            });
        var service = new InstantQuoteFileService(
            repository,
            storage,
            new CleanScanner(),
            Options.Create(new InstantQuoteFileOptions { StorageBucket = "private-bucket" }),
            new FakeTimeProvider(Now));
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("session-token")).TrimEnd('=');

        var response = await service.FinalizeAsync(
            session.Id,
            new InstantQuoteOwner(session.OwnerSubject, session.IsAuthenticated),
            token,
            "same-authority-key",
            new FinalizeInstantQuoteFilesRequest(requestId, [acquired.Record.Id]),
            CancellationToken.None);

        Assert.Equal(destination, Assert.Single(response.Files).ObjectName);
        context.ChangeTracker.Clear();
        var finalization = await context.InstantQuoteFinalizations.AsNoTracking().SingleAsync();
        Assert.Equal(InstantQuoteWorkflowState.Finalized, finalization.State);
        Assert.Equal(requestId, finalization.QuotationRequestId);
    }

    [Fact]
    public async Task Upload_ConcurrentUnknownReconciliation_BothCallersReturnAuthoritativeClean()
    {
        Guid sessionId;
        Guid fileId;
        var bytes = new byte[84];
        Encoding.ASCII.GetBytes("binary stl").CopyTo(bytes, 0);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        const string idempotencyKey = "iiiiiiiiiiiiiiii";
        const string fileName = "part.stl";
        const string contentType = "model/stl";
        var tokenBytes = Encoding.UTF8.GetBytes(new string('t', 32));
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using (var setup = await CreateMigratedContextAsync())
        {
            var repository = new InstantQuoteFileRepository(setup);
            var session = new InstantQuoteUploadSession(
                Guid.NewGuid(), "https://issuer.example|user-42", true, SHA256.HashData(tokenBytes),
                Now.AddHours(1), Now);
            sessionId = session.Id;
            await repository.CreateSessionAsync(session, CancellationToken.None);
            var upload = new InstantQuoteUploadFile(
                Guid.NewGuid(), session.Id, Hash(idempotencyKey),
                Fingerprint($"{session.Id:N}\n{fileName}\n{contentType}\n{sha}"),
                fileName, ".stl", contentType, sha, sha, bytes.Length, 101, "private-bucket",
                $"instant-quotation/temp/{session.Id:N}/part.stl", null, null,
                InstantQuoteWorkflowState.Unknown, Now, Now);
            var acquired = await repository.ReserveUploadAsync(upload, CancellationToken.None);
            fileId = acquired.Record.Id;
        }

        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var storage = new ConcurrentReconciliationStorage(bytes, sessionId, sha);
        var first = CreateReconciliationService(new InstantQuoteFileRepository(firstContext), storage);
        var second = CreateReconciliationService(new InstantQuoteFileRepository(secondContext), storage);
        var owner = new InstantQuoteOwner("https://issuer.example|user-42", true);

        var results = await Task.WhenAll(
            first.UploadAsync(sessionId, owner, token, idempotencyKey, sha, new MemoryStream(bytes),
                new InstantQuoteUploadMetadata(fileName, contentType), CancellationToken.None),
            second.UploadAsync(sessionId, owner, token, idempotencyKey, sha, new MemoryStream(bytes),
                new InstantQuoteUploadMetadata(fileName, contentType), CancellationToken.None));

        Assert.All(results, result => Assert.Equal("clean", result.Status));
        await using var verify = fixture.CreateContext();
        var stored = await verify.InstantQuoteUploadFiles.AsNoTracking().SingleAsync(value => value.Id == fileId);
        Assert.Equal(InstantQuoteWorkflowState.Clean, stored.State);
        Assert.Equal(sha, stored.ActualSha256);
    }

    [Fact]
    public async Task ReserveFinalization_ConcurrentThenCompleted_ClassifiesReplayAndConflict()
    {
        Guid sessionId;
        await using (var setupContext = await CreateMigratedContextAsync())
        {
            var session = CreateSession();
            sessionId = session.Id;
            await new InstantQuoteFileRepository(setupContext).CreateSessionAsync(session, CancellationToken.None);
        }

        var quotationRequestId = Guid.NewGuid();
        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var concurrent = await Task.WhenAll(
            new InstantQuoteFileRepository(firstContext).ReserveFinalizationAsync(
                CreateFinalization(sessionId, quotationRequestId, "same-finalization"), CancellationToken.None),
            new InstantQuoteFileRepository(secondContext).ReserveFinalizationAsync(
                CreateFinalization(sessionId, quotationRequestId, "same-finalization"), CancellationToken.None));

        Assert.Single(concurrent, result => result.Status == InstantQuoteReservationStatus.Acquired);
        Assert.Single(concurrent, result => result.Status == InstantQuoteReservationStatus.InProgress);
        Assert.Equal(concurrent[0].Record.Id, concurrent[1].Record.Id);

        await using (var completingContext = fixture.CreateContext())
        {
            var stored = await completingContext.InstantQuoteFinalizations.SingleAsync();
            stored.State = InstantQuoteWorkflowState.Finalized;
            await completingContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var replayContext = fixture.CreateContext();
        var replayRepository = new InstantQuoteFileRepository(replayContext);
        var replay = await replayRepository.ReserveFinalizationAsync(
            CreateFinalization(sessionId, quotationRequestId, "same-finalization"), CancellationToken.None);
        var conflict = await replayRepository.ReserveFinalizationAsync(
            CreateFinalization(sessionId, quotationRequestId, "changed-finalization"), CancellationToken.None);

        Assert.Equal(InstantQuoteReservationStatus.Replay, replay.Status);
        Assert.Equal(InstantQuoteReservationStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task ReserveUpload_PrimaryKeyCollision_IsNotClassifiedAsIdempotency()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        var existing = CreateUpload(session.Id, "first", id: Guid.NewGuid(), idempotencyKey: "first-key");
        await repository.ReserveUploadAsync(existing, CancellationToken.None);
        context.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => repository.ReserveUploadAsync(
            CreateUpload(session.Id, "second", existing.Id, "different-key"), CancellationToken.None));

        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("PK_InstantQuoteUploadFile", postgresException.ConstraintName);
    }

    [Fact]
    public async Task ChecksumConstraints_InvalidExpectedOrActualSha256_PostgreSqlRejectsWrite()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new InstantQuoteFileRepository(context);
        var session = CreateSession();
        await repository.CreateSessionAsync(session, CancellationToken.None);
        var acquired = await repository.ReserveUploadAsync(CreateUpload(session.Id, "checksum"), CancellationToken.None);

        var expectedException = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"InstantQuoteUploadFile\" SET \"ExpectedSha256\" = {'A'} WHERE \"Id\" = {acquired.Record.Id}"));
        var actualException = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"InstantQuoteUploadFile\" SET \"ActualSha256\" = {'B'} WHERE \"Id\" = {acquired.Record.Id}"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, expectedException.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, actualException.SqlState);
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

    private static InstantQuoteUploadFile CreateUpload(
        Guid sessionId,
        string fingerprint,
        Guid? id = null,
        string idempotencyKey = "upload-key") => new(
        id ?? Guid.NewGuid(), sessionId, Hash(idempotencyKey), Fingerprint(fingerprint), "part.stl", ".stl", "model/stl",
        new string('a', 64), null, null, null, "private-bucket",
        $"instant-quote/{sessionId:N}/{Guid.NewGuid():N}", null, null,
        InstantQuoteWorkflowState.Pending, Now, Now);

    private static InstantQuoteFinalization CreateFinalization(Guid sessionId, Guid quotationRequestId, string fingerprint) => new(
        Guid.NewGuid(), sessionId, Hash("finalize-key"), Fingerprint(fingerprint), quotationRequestId,
        [Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")], InstantQuoteWorkflowState.Pending, Now, Now);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Fingerprint(string value) => Convert.ToHexString(Hash(value)).ToLowerInvariant();

    private static InstantQuoteFileService CreateReconciliationService(
        InstantQuoteFileRepository repository,
        IInstantQuoteObjectStorage storage) => new(
        repository,
        storage,
        new CleanScanner(),
        Options.Create(new InstantQuoteFileOptions { StorageBucket = "private-bucket" }),
        new FakeTimeProvider(Now));

    private sealed class SameAuthorityRaceStorage(string destination, Func<Task> beforePromotionReturns)
        : IInstantQuoteObjectStorage
    {
        private readonly InstantQuoteObjectMetadata final = new(
            "private-bucket", destination, 202, 42, new string('a', 64));
        private bool promoted;

        public Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(
            string bucket, string objectName, Stream content, string expectedSha256,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstantQuoteObjectMetadata?> GetMetadataAsync(
            string bucket, string objectName, CancellationToken cancellationToken) =>
            Task.FromResult<InstantQuoteObjectMetadata?>(promoted ? final : null);

        public Task DownloadGenerationAsync(
            string bucket, string objectName, long generation, Stream destinationStream,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(
            string sourceBucket, string sourceObjectName, long sourceGeneration,
            string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
        {
            await beforePromotionReturns();
            promoted = true;
            return final;
        }

        public Task DeleteGenerationAsync(
            string bucket, string objectName, long generation, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CleanScanner : IInstantQuoteFileSafetyScanner
    {
        public async Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            return InstantQuoteScanResult.Clean;
        }
    }

    private sealed class ConcurrentReconciliationStorage(
        byte[] bytes,
        Guid sessionId,
        string sha256) : IInstantQuoteObjectStorage
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int downloadCount;

        public Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(
            string bucket, string objectName, Stream content, string expectedSha256,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstantQuoteObjectMetadata?> GetMetadataAsync(
            string bucket, string objectName, CancellationToken cancellationToken) =>
            Task.FromResult<InstantQuoteObjectMetadata?>(new InstantQuoteObjectMetadata(
                "private-bucket", $"instant-quotation/temp/{sessionId:N}/part.stl", 101, bytes.Length, sha256));

        public async Task DownloadGenerationAsync(
            string bucket, string objectName, long generation, Stream destination,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref downloadCount) == 2)
            {
                release.TrySetResult();
            }
            await release.Task.WaitAsync(cancellationToken);
            await destination.WriteAsync(bytes, cancellationToken);
        }

        public Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(
            string sourceBucket, string sourceObjectName, long sourceGeneration,
            string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteGenerationAsync(
            string bucket, string objectName, long generation, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
