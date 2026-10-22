using Legacy.Maliev.FileService.Application.CustomerDocuments;
namespace Legacy.Maliev.FileService.Api.CustomerDocuments;
/// <summary>Runs an explicitly enabled daily internal worklist evaluation; defaults remain disabled.</summary>
public sealed class NdaReminderHostedService(IServiceScopeFactory scopeFactory, NdaOptions options, TimeProvider clock,
    ILogger<NdaReminderHostedService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.SchedulerEnabled) return;
        TimeZoneInfo zone;
        try
        {
            if (string.IsNullOrWhiteSpace(options.TimeZoneId)) throw new TimeZoneNotFoundException();
            zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogError("Internal NDA reminder evaluation requires a valid explicit timezone.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested && options.Enabled && options.SchedulerEnabled)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<INdaReminderQueue>().QueueDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Exception payloads may contain provider details; retain only the actionable operational signal.
                logger.LogWarning("Internal NDA reminder evaluation is uncertain; responsible staff must review the worklist.");
            }
            var now = clock.GetUtcNow();
            var midnight = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
            if (zone.IsInvalidTime(midnight) || zone.IsAmbiguousTime(midnight))
            {
                logger.LogError("Internal NDA reminder midnight is ambiguous; explicit calendar review is required.");
                return;
            }
            var next = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);
            try { await Task.Delay(next - now, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
