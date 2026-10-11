using System.Text.Json;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;
using Moq;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class RedisUploadAcquisitionOutcomeTests
{
    private const string Key = "legacy:file:idempotency:v1:MODEL";
    private const string Lease = Key + ":lease";

    [Fact]
    public async Task AbortedInitialTransaction_WithMissingCheckpoint_FailsWithoutRetryOrOwnership()
    {
        var model = new Model(RedisValue.Null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.Store.AcquireAsync("MODEL", "payload", "orders/model", default));

        Assert.Equal("Upload checkpoint changed during acquisition.", failure.Message);
        model.Transaction.Verify(t => t.ExecuteAsync(CommandFlags.None), Times.Once);
        model.Database.Verify(d => d.StringGetAsync(Key, CommandFlags.None), Times.Once);
        model.Database.Verify(d => d.StringGetAsync(Lease, CommandFlags.None), Times.Never);
        model.Database.Verify(d => d.CreateTransaction(null), Times.Once);

        var profile = new ProfilingSession(null);
        var lifetime = new RedisUploadIdempotencyStoreTests.FirstAcquisitionProfile(profile);
        Func<ProfilingSession> callback = lifetime.GetActiveSession;
        Assert.Same(profile, callback());
        var cleanupFailure = new IOException("Synthetic diagnostic cleanup failure.");
        var diagnosticFailures = new List<Exception>();
        try
        {
            Assert.Same(profile, callback());
        }
        finally
        {
            lifetime.Stop();
            RedisUploadIdempotencyStoreTests.CaptureDiagnostic(() => throw cleanupFailure, diagnosticFailures);
        }
        Assert.Null(callback());
        lifetime.Stop();
        Assert.Null(callback());
        var aggregate = Assert.Throws<AggregateException>(() =>
            RedisUploadIdempotencyStoreTests.CompleteDiagnostics(failure, diagnosticFailures));
        Assert.Collection(aggregate.InnerExceptions,
            primary => Assert.Same(failure, primary), cleanup => Assert.Same(cleanupFailure, cleanup));
    }

    [Fact]
    public async Task ExistingDifferentFingerprint_IsContentionRatherThanMissingState()
    {
        var model = new Model(Envelope("other-payload", "pending"));

        var result = await model.Store.AcquireAsync("MODEL", "payload", "ignored", default);

        Assert.Equal(UploadAcquireState.Conflict, result.State);
        model.Database.Verify(d => d.StringGetAsync(Lease, CommandFlags.None), Times.Never);
    }

    [Fact]
    public async Task PendingMatchingLease_RetainsOriginalPathWithoutAcquiring()
    {
        var model = new Model(Envelope("payload", "pending"), "owner");

        var result = await model.Store.AcquireAsync("MODEL", "payload", "ignored", default);

        Assert.Equal(UploadAcquireState.InProgress, result.State);
        Assert.Null(result.ReservationId);
        Assert.Equal("orders/original", result.EffectivePath);
        model.Database.Verify(d => d.CreateTransaction(null), Times.Once);
    }

    [Fact]
    public async Task PendingMissingLease_BecomesUnknownWithoutGrantingNewOwnership()
    {
        var model = new Model(Envelope("payload", "pending"));

        var result = await model.Store.AcquireAsync("MODEL", "payload", "ignored", default);

        Assert.Equal(UploadAcquireState.Unknown, result.State);
        Assert.Equal("owner", result.ReservationId);
        Assert.Equal("orders/original", result.EffectivePath);
        model.Database.Verify(d => d.CreateTransaction(null), Times.Exactly(2));
    }

    [Fact]
    public async Task UnknownCheckpoint_RetainsReservationAndResponseWithoutReexecution()
    {
        var response = Response();
        var model = new Model(Envelope("payload", "unknown", response));

        var result = await model.Store.AcquireAsync("MODEL", "payload", "ignored", default);

        Assert.Equal(UploadAcquireState.Unknown, result.State);
        Assert.Equal("owner", result.ReservationId);
        Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(result.Response));
        model.Database.Verify(d => d.CreateTransaction(null), Times.Once);
    }

    [Fact]
    public async Task CompletedCheckpoint_ReplaysExactResponseWithoutNewReservation()
    {
        var response = Response();
        var model = new Model(Envelope("payload", "completed", response));

        var result = await model.Store.AcquireAsync("MODEL", "payload", "ignored", default);

        Assert.Equal(UploadAcquireState.Replay, result.State);
        Assert.Null(result.ReservationId);
        Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(result.Response));
        model.Database.Verify(d => d.CreateTransaction(null), Times.Once);
    }

    [Fact]
    public async Task StaleWorker_CannotRenewAnotherReservationsLease()
    {
        var model = new Model(Envelope("payload", "pending"), "owner");

        Assert.False(await model.Store.RenewAsync("MODEL", "stale-owner", default));

        model.Database.Verify(d => d.CreateTransaction(null), Times.Never);
        model.Database.Verify(d => d.StringGetAsync(Lease, CommandFlags.None), Times.Never);
    }

    private static UploadResultResponse Response() => new([
        new("private-model", "orders/original", new Uri("https://storage.test/model?token=synthetic")),
    ]);

    private static RedisValue Envelope(string fingerprint, string state, UploadResultResponse? response = null) =>
        JsonSerializer.Serialize(new { fingerprint, reservationId = "owner", state, response, effectivePath = "orders/original" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class Model
    {
        public Mock<IDatabase> Database { get; } = new(MockBehavior.Strict);
        public Mock<ITransaction> Transaction { get; } = new();
        public RedisUploadIdempotencyStore Store { get; }

        public Model(RedisValue checkpoint, RedisValue lease = default)
        {
            Transaction.Setup(t => t.ExecuteAsync(CommandFlags.None)).ReturnsAsync(false);
            Database.Setup(d => d.CreateTransaction(null)).Returns(Transaction.Object);
            Database.Setup(d => d.StringGetAsync(Key, CommandFlags.None)).ReturnsAsync(checkpoint);
            Database.Setup(d => d.StringGetAsync(Lease, CommandFlags.None)).ReturnsAsync(lease);
            var connection = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
            connection.Setup(c => c.GetDatabase(-1, null)).Returns(Database.Object);
            Store = new RedisUploadIdempotencyStore(connection.Object);
        }
    }
}
