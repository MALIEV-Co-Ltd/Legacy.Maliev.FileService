using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Application.Models;
using System.Text.Json;
using StackExchange.Redis;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class RedisUploadIdempotencyStoreTests : IAsyncLifetime
{
    private readonly IContainer container = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private IConnectionMultiplexer? connection;
    public async Task InitializeAsync() { await container.StartAsync(); connection = await ConnectionMultiplexer.ConnectAsync($"{container.Hostname}:{container.GetMappedPublicPort(6379)},abortConnect=false"); }
    public async Task DisposeAsync() { if (connection is not null) { await connection.CloseAsync(); connection.Dispose(); } await container.DisposeAsync(); }

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
        var owner = Assert.Single(results.Where(result => result.State == UploadAcquireState.Acquired));
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
        var old = await store.AcquireAsync("FENCE", "old-payload", "orders/old", default);
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

    private static UploadResultResponse Response() => new([new("maliev.com", "orders/part.stl", new Uri("https://storage.test/signed?token=exact"))]);
}
