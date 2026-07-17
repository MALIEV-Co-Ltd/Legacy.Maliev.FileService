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
}
