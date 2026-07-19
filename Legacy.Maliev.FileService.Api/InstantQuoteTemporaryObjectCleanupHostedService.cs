using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Api;

/// <summary>Schedules bounded temporary-object cleanup only when explicitly enabled.</summary>
public sealed class InstantQuoteTemporaryObjectCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<InstantQuoteFileOptions> options,
    TimeProvider timeProvider,
    ILogger<InstantQuoteTemporaryObjectCleanupHostedService> logger) : BackgroundService
{
    private readonly InstantQuoteFileOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.WritesEnabled || !_options.CleanupEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.CleanupInterval, timeProvider);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<InstantQuoteTemporaryObjectCleanupService>()
                    .RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Instant quotation temporary-object cleanup sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
