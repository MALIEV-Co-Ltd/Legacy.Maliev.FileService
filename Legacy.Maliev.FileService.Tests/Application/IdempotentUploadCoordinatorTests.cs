using System.Text.Json;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class IdempotentUploadCoordinatorTests
{
    [Fact]
    public async Task ResponseLossRetry_ReplaysExactSignedResponseWithoutSecondExecution()
    {
        var store = new MemoryStore(); var coordinator = new IdempotentUploadCoordinator(store); var executions = 0;
        var first = await Run(bytes: [1, 2, 3]);
        var replay = await Run(bytes: [1, 2, 3]);
        Assert.Equal(1, executions);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));

        Task<UploadResultResponse> Run(byte[] bytes) => coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", "orders/42", [new File("part.stl", "model/stl", bytes)], (_, _, _) =>
        { executions++; return Task.FromResult(Response()); }, (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default);
    }

    [Fact]
    public async Task SameWorkflowWithDifferentBytes_ConflictsWithoutExecution()
    {
        var store = new MemoryStore(); var coordinator = new IdempotentUploadCoordinator(store); var executions = 0;
        await coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])], Execute, Reconcile, default);
        await Assert.ThrowsAsync<UploadIdempotencyConflictException>(() => coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [2])], Execute, Reconcile, default));
        Assert.Equal(1, executions);
        Task<UploadResultResponse> Execute(Guid _, string? __, CancellationToken ___) { executions++; return Task.FromResult(Response()); }
        static Task<UploadResultResponse?> Reconcile(Guid _, string? __, CancellationToken ___) => Task.FromResult<UploadResultResponse?>(null);
    }

    [Fact]
    public async Task SameWorkflowKey_IsIsolatedAcrossSignedPrincipals()
    {
        var store = new MultiIdentityMemoryStore(); var coordinator = new IdempotentUploadCoordinator(store); var executions = 0;
        foreach (var principal in new[] { "intranet-service-a", "intranet-service-b" })
            await coordinator.ExecuteAsync(principal, "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])], (_, _, _) => { executions++; return Task.FromResult(Response()); }, (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default);
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task ConcurrentReplay_HasOneExecutorAndExplicitInProgressResult()
    {
        var store = new MemoryStore(); var coordinator = new IdempotentUploadCoordinator(store); var gate = new TaskCompletionSource<UploadResultResponse>();
        var first = coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])], (_, _, _) => gate.Task, (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default);
        await store.Acquired.Task;
        await Assert.ThrowsAsync<UploadIdempotencyInProgressException>(() => coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])], (_, _, _) => throw new InvalidOperationException(), (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default));
        gate.SetResult(Response()); await first;
    }

    [Fact]
    public async Task LeaseLoss_FailsClosedBeforeExecutorAndRetainsUnknownState()
    {
        var store = new MemoryStore { LoseRenewal = true }; var coordinator = new IdempotentUploadCoordinator(store); var executed = false;
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])], (_, _, _) => { executed = true; return Task.FromResult(Response()); }, (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default));
        Assert.False(executed); Assert.Equal("unknown", store.State);
    }

    [Fact]
    public async Task InitialRenewalFailure_ReleasesReservationAndReportsUnavailable()
    {
        var store = new MemoryStore { ThrowRenewal = true };
        var coordinator = new IdempotentUploadCoordinator(store);
        var executed = false;

        await Assert.ThrowsAsync<UploadIdempotencyUnavailableException>(() => coordinator.ExecuteAsync(
            "intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])],
            (_, _, _) => { executed = true; return Task.FromResult(Response()); },
            (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default));

        Assert.False(executed);
        Assert.Equal(1, store.ReleaseCount);
        Assert.Null(store.State);
    }

    [Fact]
    public async Task MidExecutionLeaseLoss_CancelsExecutorAndRetainsUnknownState()
    {
        var store = new MemoryStore { RenewalsBeforeLoss = 1 };
        var coordinator = new IdempotentUploadCoordinator(store) { RenewalInterval = TimeSpan.FromMilliseconds(10) };
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => coordinator.ExecuteAsync(
            "intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])],
            async (_, _, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(); },
            (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default));
        Assert.Equal("unknown", store.State);
    }

    [Fact]
    public async Task UnknownRetry_ReconcilesWithSameDeterministicGeneration()
    {
        var store = new MemoryStore(); var coordinator = new IdempotentUploadCoordinator(store); Guid firstGeneration = default; Guid reconciledGeneration = default;
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => coordinator.ExecuteAsync(
            "intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])],
            (generation, _, _) => { firstGeneration = generation; throw new IOException("response lost"); },
            (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default));
        var recovered = await coordinator.ExecuteAsync(
            "intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])],
            (_, _, _) => throw new InvalidOperationException("must not execute twice"),
            (generation, _, _) => { reconciledGeneration = generation; return Task.FromResult<UploadResultResponse?>(Response()); }, default);
        Assert.Equal(firstGeneration, reconciledGeneration);
        Assert.Equal(JsonSerializer.Serialize(Response()), JsonSerializer.Serialize(recovered));
    }

    [Fact]
    public async Task UnknownRetryAcrossUtcMidnight_ReusesPersistedDefaultPath()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 17, 23, 59, 0, TimeSpan.Zero));
        var store = new MemoryStore();
        var coordinator = new IdempotentUploadCoordinator(store) { Clock = clock };
        string? executionPath = null;
        string? reconciliationPath = null;

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => coordinator.ExecuteAsync(
            "intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])],
            (_, path, _) => { executionPath = path; throw new IOException("response lost"); },
            (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default));

        clock.Advance(TimeSpan.FromDays(1));
        await coordinator.ExecuteAsync(
            "intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])],
            (_, _, _) => throw new InvalidOperationException("must not execute twice"),
            (_, path, _) => { reconciliationPath = path; return Task.FromResult<UploadResultResponse?>(Response()); }, default);

        Assert.Equal(executionPath, reconciliationPath);
        Assert.StartsWith("uploads/2026-7-17/", executionPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionCancellation_RetainsExactResponseAndRetryResolvesWithoutExecution()
    {
        var store = new MemoryStore { ThrowCompletionOnce = true }; var coordinator = new IdempotentUploadCoordinator(store); var executions = 0;
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => Run());
        var replay = await Run();
        Assert.Equal(1, executions); Assert.Equal("completed", store.State);
        Assert.Equal(JsonSerializer.Serialize(Response()), JsonSerializer.Serialize(replay));
        Task<UploadResultResponse> Run() => coordinator.ExecuteAsync("intranet-service", "workflow-42", "maliev.com", null, [new File("part.stl", "model/stl", [1])], (_, _, _) => { executions++; return Task.FromResult(Response()); }, (_, _, _) => Task.FromResult<UploadResultResponse?>(null), default);
    }

    private static UploadResultResponse Response() => new([new("maliev.com", "orders/part.stl", new Uri("https://storage.test/signed"))]);
    private sealed class File(string name, string type, byte[] bytes) : IUploadFile { public string FileName => name; public string ContentType => type; public long Length => bytes.Length; public Stream OpenReadStream() => new MemoryStream(bytes, false); }

    private sealed class MemoryStore : IUploadIdempotencyStore
    {
        private readonly object sync = new(); private string? fingerprint; private string? reservation; private UploadResultResponse? response;
        private int renewals;
        public bool LoseRenewal { get; init; }
        public bool ThrowRenewal { get; init; }
        public int? RenewalsBeforeLoss { get; init; }
        public bool ThrowCompletionOnce { get; init; }
        public int ReleaseCount { get; private set; }
        public string? State { get; private set; }
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? effectivePath;
        public Task<UploadAcquireResult> AcquireAsync(string identity, string value, string path, CancellationToken c)
        {
            lock (sync)
            {
                if (State is null) { fingerprint = value; effectivePath = path; reservation = Guid.NewGuid().ToString("N"); State = "pending"; Acquired.TrySetResult(); return Task.FromResult(new UploadAcquireResult(UploadAcquireState.Acquired, reservation, EffectivePath: effectivePath)); }
                if (fingerprint != value) return Task.FromResult(new UploadAcquireResult(UploadAcquireState.Conflict));
                return Task.FromResult<UploadAcquireResult>(State switch { "completed" => new(UploadAcquireState.Replay, Response: response, EffectivePath: effectivePath), "unknown" => new(UploadAcquireState.Unknown, reservation, response, effectivePath), _ => new(UploadAcquireState.InProgress, EffectivePath: effectivePath) });
            }
        }
        public Task<bool> RenewAsync(string identity, string id, CancellationToken c)
        {
            if (ThrowRenewal) throw new InvalidOperationException("Redis unavailable");
            var owned = !LoseRenewal && State == "pending" && reservation == id;
            if (RenewalsBeforeLoss is not null && renewals++ >= RenewalsBeforeLoss) owned = false;
            return Task.FromResult(owned);
        }
        public Task CompleteAsync(string identity, string value, string id, UploadResultResponse result, CancellationToken c)
        {
            lock (sync)
            {
                if (ThrowCompletionOnce && State == "pending") throw new OperationCanceledException("completion response lost");
                response = result; State = "completed";
            }
            return Task.CompletedTask;
        }
        public Task MarkUnknownAsync(string identity, string id, UploadResultResponse? result, CancellationToken c) { lock (sync) { response = result; State = "unknown"; } return Task.CompletedTask; }
        public Task ReleaseAsync(string identity, string id, CancellationToken c) { lock (sync) { ReleaseCount++; State = null; fingerprint = null; reservation = null; effectivePath = null; } return Task.CompletedTask; }
    }

    private sealed class MultiIdentityMemoryStore : IUploadIdempotencyStore
    {
        private readonly Dictionary<string, (string Fingerprint, string Reservation, UploadResultResponse? Response)> values = [];
        public Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, string path, CancellationToken c) { if (!values.TryGetValue(identity, out var value)) { var reservation = Guid.NewGuid().ToString("N"); values[identity] = (fingerprint, reservation, null); return Task.FromResult(new UploadAcquireResult(UploadAcquireState.Acquired, reservation, EffectivePath: path)); } return Task.FromResult(new UploadAcquireResult(UploadAcquireState.Replay, Response: value.Response, EffectivePath: path)); }
        public Task<bool> RenewAsync(string identity, string id, CancellationToken c) => Task.FromResult(true);
        public Task CompleteAsync(string identity, string fingerprint, string id, UploadResultResponse response, CancellationToken c) { values[identity] = (fingerprint, id, response); return Task.CompletedTask; }
        public Task MarkUnknownAsync(string identity, string id, UploadResultResponse? response, CancellationToken c) => Task.CompletedTask;
        public Task ReleaseAsync(string identity, string id, CancellationToken c) => Task.CompletedTask;
    }
}
