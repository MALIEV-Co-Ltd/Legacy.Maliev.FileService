using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class NdaReminderHostedServiceTests
{
    [Fact]
    public async Task EnabledWorkerQueuesImmediatelyAndAgainAtBangkokMidnight()
    {
        var queue = new ObservedQueue();
        var clock = new ObservedClock(new(2026, 10, 8, 16, 59, 0, TimeSpan.Zero));
        using var services = Services(queue);
        using var worker = Worker(services, queue, clock, true);
        await worker.StartAsync(default);
        await Await(queue.First.Task);
        Assert.Equal(1, queue.Calls);
        await Await(clock.Timer.Task);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Await(queue.Second.Task);
        Assert.Equal(2, queue.Calls);
        await worker.StopAsync(default);
    }
    [Fact]
    public async Task QueueFaultIsObservedAndNextDailyTickStillRuns()
    {
        var queue = new ObservedQueue { FailFirst = true };
        var clock = new ObservedClock(new(2026, 10, 8, 16, 59, 0, TimeSpan.Zero));
        using var services = Services(queue);
        using var worker = Worker(services, queue, clock, true);
        await worker.StartAsync(default);
        await Await(queue.First.Task);
        await Await(clock.Timer.Task);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Await(queue.Second.Task);
        Assert.Equal(2, queue.Calls);
        await worker.StopAsync(default);
    }
    [Fact]
    public async Task DisabledWorkerDoesNotQueueOrCreateTimer()
    {
        var queue = new ObservedQueue();
        var clock = new ObservedClock(new(2026, 10, 8, 16, 59, 0, TimeSpan.Zero));
        using var services = Services(queue);
        using var worker = Worker(services, queue, clock, false);
        await worker.StartAsync(default);
        Assert.Equal(0, queue.Calls);
        Assert.False(clock.Timer.Task.IsCompleted);
        await worker.StopAsync(default);
    }
    private static NdaReminderHostedService Worker(ServiceProvider services, ObservedQueue queue, TimeProvider clock, bool enabled)
    {
        _ = queue;
        return new(services.GetRequiredService<IServiceScopeFactory>(), new NdaOptions { Enabled = enabled, SchedulerEnabled = enabled },
            clock, NullLogger<NdaReminderHostedService>.Instance);
    }
    private static ServiceProvider Services(ObservedQueue queue) => new ServiceCollection().AddScoped<INdaReminderQueue>(_ => queue).BuildServiceProvider();
    private static async Task Await(Task task)
    {
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(task.IsCompletedSuccessfully, "Daily evaluation did not reach the expected clock-controlled boundary.");
    }
    private sealed class ObservedQueue : INdaReminderQueue
    {
        public int Calls { get; private set; }
        public bool FailFirst { get; set; }
        public TaskCompletionSource<bool> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> QueueDueAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1) First.TrySetResult(true);
            if (Calls == 2) Second.TrySetResult(true);
            return Calls == 1 && FailFirst ? Task.FromException<int>(new DocumentAuthorityUnavailableException()) : Task.FromResult(1);
        }
    }
    private sealed class ObservedClock(DateTimeOffset now) : TimeProvider
    {
        private readonly FakeTimeProvider inner = new(now);
        public TaskCompletionSource<bool> Timer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public void Advance(TimeSpan amount) => inner.Advance(amount);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Timer.TrySetResult(true);
            return timer;
        }
    }
}
