using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Application.Models;
using System.Text.Json;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using Moq;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class RedisUploadIdempotencyStoreTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly IContainer container = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private IConnectionMultiplexer? connection;
    public async Task InitializeAsync() { await container.StartAsync(); connection = await ConnectionMultiplexer.ConnectAsync($"{container.Hostname}:{container.GetMappedPublicPort(6379)},abortConnect=false"); }
    public async Task DisposeAsync() { if (connection is not null) { await connection.CloseAsync(); connection.Dispose(); } await container.DisposeAsync(); }

    [Fact]
    public async Task ExpiryTransition_RechecksLeaseAndSnapshotAtomically()
    {
        var database = connection!.GetDatabase();
        RedisKey key = "legacy:file:idempotency:v1:EXPIRY-RACE";
        RedisKey lease = "legacy:file:idempotency:v1:EXPIRY-RACE:lease";
        await database.StringSetAsync(key, "old", TimeSpan.FromHours(24));
        await database.StringSetAsync(lease, "owner", TimeSpan.FromMinutes(1));

        Assert.False(await RedisUploadIdempotencyStore.ReplaceExpiredAsync(database, key, lease, "old", "unknown", "owner", default));
        Assert.Equal("old", (string?)await database.StringGetAsync(key));

        await database.KeyDeleteAsync(lease);
        await database.StringSetAsync(key, "new", TimeSpan.FromHours(24));
        Assert.False(await RedisUploadIdempotencyStore.ReplaceExpiredAsync(database, key, lease, "old", "unknown", "owner", default));
        Assert.Equal("new", (string?)await database.StringGetAsync(key));

        Assert.True(await RedisUploadIdempotencyStore.ReplaceExpiredAsync(database, key, lease, "new", "unknown", "owner", default));
        Assert.Equal("unknown", (string?)await database.StringGetAsync(key));
        var ttl = await database.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.GetValueOrDefault(), TimeSpan.FromHours(23), TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task Acquire_AfterLeaseReadCannotDowngradeAReappearingOwner()
    {
        const string identity = "EXPIRY-PUBLIC-RACE";
        var realDatabase = connection!.GetDatabase();
        var ownerStore = new RedisUploadIdempotencyStore(connection);
        var owner = await ownerStore.AcquireAsync(identity, "fingerprint", "orders/42", default);
        RedisKey checkpointKey = $"legacy:file:idempotency:v1:{identity}";
        RedisKey leaseKey = $"legacy:file:idempotency:v1:{identity}:lease";
        await realDatabase.KeyDeleteAsync(leaseKey);
        var original = await realDatabase.StringGetAsync(checkpointKey);
        var readObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(d => d.CreateTransaction(null)).Returns(() => realDatabase.CreateTransaction());
        database.Setup(d => d.StringGetAsync(checkpointKey, CommandFlags.None))
            .Returns(() => realDatabase.StringGetAsync(checkpointKey));
        database.Setup(d => d.StringGetAsync(leaseKey, CommandFlags.None)).Returns(async () =>
        {
            var observed = await realDatabase.StringGetAsync(leaseKey);
            readObserved.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return observed;
        });
        var multiplexer = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        multiplexer.Setup(c => c.GetDatabase(-1, null)).Returns(database.Object);
        var retry = new RedisUploadIdempotencyStore(multiplexer.Object).AcquireAsync(identity, "fingerprint", "ignored", default);
        try
        {
            await readObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await realDatabase.StringSetAsync(leaseKey, owner.ReservationId!, TimeSpan.FromMinutes(1));
            release.TrySetResult();
            Assert.Equal(UploadAcquireState.Unknown, (await retry.WaitAsync(TimeSpan.FromSeconds(10))).State);
            Assert.Equal(original, await realDatabase.StringGetAsync(checkpointKey));
            Assert.True(await ownerStore.RenewAsync(identity, owner.ReservationId!, default));
        }
        finally
        {
            release.TrySetResult();
            await retry.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ExpiredWorkerLease_TransitionsDurableCheckpointToUnknownWithoutReacquiring()
    {
        const string identity = "IDENTITY"; const string path = "uploads/2026-7-17/generation"; var store = new RedisUploadIdempotencyStore(connection); var first = await store.AcquireAsync(identity, "fingerprint", path, default);
        Assert.Equal(UploadAcquireState.Acquired, first.State);
        await connection!.GetDatabase().KeyDeleteAsync("legacy:file:idempotency:v1:IDENTITY:lease");
        var afterCrash = await store.AcquireAsync(identity, "fingerprint", "uploads/2026-7-18/different", default);
        var retry = await store.AcquireAsync(identity, "fingerprint", "uploads/2026-7-18/different", default);
        Assert.Equal(UploadAcquireState.Unknown, afterCrash.State); Assert.Equal(UploadAcquireState.Unknown, retry.State);
        Assert.Equal(path, afterCrash.EffectivePath); Assert.Equal(path, retry.EffectivePath);
        Assert.True(await connection.GetDatabase().KeyTimeToLiveAsync("legacy:file:idempotency:v1:IDENTITY") >= TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task CompletedCheckpoint_ReplaysExactSignedResponseAndRejectsDifferentPayload()
    {
        const string path = "orders/42"; var store = new RedisUploadIdempotencyStore(connection); var acquired = await store.AcquireAsync("REPLAY", "fingerprint", path, default);
        var response = new UploadResultResponse([new("maliev.com", "orders/part.stl", new Uri("https://storage.test/signed?token=exact"))]);
        await store.CompleteAsync("REPLAY", "fingerprint", acquired.ReservationId!, response, default);
        var replay = await store.AcquireAsync("REPLAY", "fingerprint", "changed-path", default); var conflict = await store.AcquireAsync("REPLAY", "changed", path, default);
        Assert.Equal(UploadAcquireState.Replay, replay.State); Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(replay.Response)); Assert.Equal(UploadAcquireState.Conflict, conflict.State);
        Assert.Equal(path, replay.EffectivePath);
    }

    [Fact]
    public async Task ConcurrentAcquisition_OneOwnerAndEveryOtherCallerInProgress()
    {
        var store = new RedisUploadIdempotencyStore(connection);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.AcquireAsync("RACE", "fingerprint", "orders/42", default)));
        var owner = Assert.Single(results, result => result.State == UploadAcquireState.Acquired);
        Assert.Equal(11, results.Count(result => result.State == UploadAcquireState.InProgress));
        Assert.Equal(owner.ReservationId, (string?)await connection!.GetDatabase().StringGetAsync("legacy:file:idempotency:v1:RACE:lease"));
        Assert.True(await store.RenewAsync("RACE", owner.ReservationId!, default));
    }

    [Theory]
    [InlineData("renew")]
    [InlineData("release")]
    [InlineData("unknown")]
    [InlineData("complete")]
    public async Task SupersededWorker_CannotChangeNewReservationOrItsLease(string operation)
    {
        var store = new RedisUploadIdempotencyStore(connection);
        var old = await AcquireFirstWithDiagnosticsAsync(store);
        await store.ReleaseAsync("FENCE", old.ReservationId!, default);
        var current = await store.AcquireAsync("FENCE", "new-payload", "orders/new", default);
        var database = connection!.GetDatabase();
        var before = await database.StringGetAsync("legacy:file:idempotency:v1:FENCE");
        switch (operation)
        {
            case "renew": Assert.False(await store.RenewAsync("FENCE", old.ReservationId!, default)); break;
            case "release": await store.ReleaseAsync("FENCE", old.ReservationId!, default); break;
            case "unknown": await store.MarkUnknownAsync("FENCE", old.ReservationId!, Response(), default); break;
            case "complete": await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync("FENCE", "old-payload", old.ReservationId!, Response(), default)); break;
        }
        Assert.Equal(before, await database.StringGetAsync("legacy:file:idempotency:v1:FENCE"));
        Assert.Equal(current.ReservationId, (string?)await database.StringGetAsync("legacy:file:idempotency:v1:FENCE:lease"));
        Assert.Equal(UploadAcquireState.InProgress, (await store.AcquireAsync("FENCE", "new-payload", "ignored", default)).State);
        Assert.True(await store.RenewAsync("FENCE", current.ReservationId!, default));
    }

    [Fact]
    public async Task ExpiredLease_StaleWorkerCannotRenewReleaseOrCompletePendingCheckpoint()
    {
        var store = new RedisUploadIdempotencyStore(connection);
        var owner = await store.AcquireAsync("EXPIRED", "fingerprint", "orders/42", default);
        var database = connection!.GetDatabase();
        await database.KeyDeleteAsync("legacy:file:idempotency:v1:EXPIRED:lease");
        var before = await database.StringGetAsync("legacy:file:idempotency:v1:EXPIRED");
        Assert.False(await store.RenewAsync("EXPIRED", owner.ReservationId!, default));
        await store.ReleaseAsync("EXPIRED", owner.ReservationId!, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync("EXPIRED", "fingerprint", owner.ReservationId!, Response(), default));
        Assert.Equal(before, await database.StringGetAsync("legacy:file:idempotency:v1:EXPIRED"));
        var retry = await store.AcquireAsync("EXPIRED", "fingerprint", "ignored", default);
        Assert.Equal(UploadAcquireState.Unknown, retry.State);
        Assert.Equal(owner.ReservationId, retry.ReservationId);
        Assert.Equal("orders/42", retry.EffectivePath);
        Assert.False(await database.KeyExistsAsync("legacy:file:idempotency:v1:EXPIRED:lease"));
    }

    [Fact]
    public async Task UnknownResponse_FinalizesExactResponseWithoutReexecutionOrNewLease()
    {
        var store = new RedisUploadIdempotencyStore(connection);
        var owner = await store.AcquireAsync("UNKNOWN", "fingerprint", "orders/42", default);
        var response = Response();
        await store.MarkUnknownAsync("UNKNOWN", owner.ReservationId!, response, default);
        var unknown = await store.AcquireAsync("UNKNOWN", "fingerprint", "ignored", default);
        Assert.Equal(UploadAcquireState.Unknown, unknown.State);
        Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(unknown.Response));
        await store.CompleteAsync("UNKNOWN", "fingerprint", unknown.ReservationId!, unknown.Response!, default);
        var replay = await store.AcquireAsync("UNKNOWN", "fingerprint", "ignored", default);
        Assert.Equal(UploadAcquireState.Replay, replay.State);
        Assert.Equal("orders/42", replay.EffectivePath);
        Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(replay.Response));
        Assert.False(await connection!.GetDatabase().KeyExistsAsync("legacy:file:idempotency:v1:UNKNOWN:lease"));
    }

    private async Task<UploadAcquireResult> AcquireFirstWithDiagnosticsAsync(RedisUploadIdempotencyStore store)
    {
        var profile = new ProfilingSession(null);
        var profileLifetime = new FirstAcquisitionProfile(profile);
        var clock = Stopwatch.StartNew();
        var diagnosticFailures = new List<Exception>();
        Exception? primaryFailure = null;
        try
        {
            CaptureDiagnostic(() => connection!.RegisterProfiler(profileLifetime.GetActiveSession), diagnosticFailures);
            CaptureDiagnostic(() => output.WriteLine(JsonSerializer.Serialize(new
            { phase = "first-acquire-start", connected = connection!.IsConnected })), diagnosticFailures);
            var result = await store.AcquireAsync("FENCE", "old-payload", "orders/old", default);
            CaptureDiagnostic(() => output.WriteLine(JsonSerializer.Serialize(new
            { phase = "first-acquire-result", state = result.State.ToString() })), diagnosticFailures);
            return result;
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            CaptureDiagnostic(() => output.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "first-acquire-failure",
                category = failure.GetType().Name,
                abortedCreateThenMissingCheckpoint = failure is InvalidOperationException
                    && failure.Message == "Upload checkpoint changed during acquisition.",
            })), diagnosticFailures);
            throw;
        }
        finally
        {
            // Stop new collection before finishing or emitting diagnostics. The
            // registered provider returns null for every later fixture operation.
            profileLifetime.Stop();
            // Only command names and durations: never endpoints, keys, arguments,
            // fingerprints, response bodies or exception messages.
            CaptureDiagnostic(() =>
            {
                var commands = profile.FinishProfiling().Take(32).Select(command => new
                {
                    command = command.Command,
                    elapsedMs = command.ElapsedTime.TotalMilliseconds,
                    enqueueMs = command.CreationToEnqueued.TotalMilliseconds,
                    sendMs = command.EnqueuedToSending.TotalMilliseconds,
                    responseMs = command.SentToResponse.TotalMilliseconds,
                    completionMs = command.ResponseToCompletion.TotalMilliseconds,
                }).ToArray();
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "first-acquire-end",
                    elapsedMs = clock.Elapsed.TotalMilliseconds,
                    connected = connection!.IsConnected,
                    commands,
                }));
            }, diagnosticFailures);
            CompleteDiagnostics(primaryFailure, diagnosticFailures);
        }
    }

    internal sealed class FirstAcquisitionProfile(ProfilingSession session)
    {
        private ProfilingSession? active = session;

        // Redis accepts a null session to disable profiling for a command.
        public ProfilingSession GetActiveSession() => Volatile.Read(ref active)!;

        public void Stop() => Interlocked.Exchange(ref active, null);
    }

    internal static void CaptureDiagnostic(Action action, List<Exception> failures)
    {
        try { action(); }
        catch (Exception failure) { failures.Add(failure); }
    }

    internal static void CompleteDiagnostics(Exception? primaryFailure, List<Exception> diagnosticFailures)
    {
        if (diagnosticFailures.Count != 0)
            throw new AggregateException("Redis test diagnostics failed.", primaryFailure is null
                ? diagnosticFailures : new[] { primaryFailure }.Concat(diagnosticFailures));
    }

    private static UploadResultResponse Response() => new([new("maliev.com", "orders/part.stl", new Uri("https://storage.test/signed?token=exact"))]);
}
