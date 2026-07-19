using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Api;

/// <summary>Checks enabled instant-quotation storage and scanner dependencies without mutating them.</summary>
public sealed class InstantQuoteFilesReadinessHealthCheck(
    IInstantQuoteScannerReadinessProbe scanner,
    IServiceProviderIsService registeredServices,
    IOptions<InstantQuoteFileOptions> options) : IHealthCheck
{
    private static readonly TimeSpan MaximumProbeDuration = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = options.Value;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.OperationTimeout < MaximumProbeDuration
                ? settings.OperationTimeout
                : MaximumProbeDuration);

            if (!registeredServices.IsService(typeof(IInstantQuoteObjectStorage)))
            {
                return HealthCheckResult.Unhealthy("storage adapter unavailable");
            }

            await scanner.CheckAsync(timeout.Token);
            return HealthCheckResult.Healthy("dependencies available");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("dependency unavailable");
        }
    }
}
