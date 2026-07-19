using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Api;

/// <summary>Checks enabled instant-quotation storage and scanner dependencies without mutating them.</summary>
public sealed class InstantQuoteFilesReadinessHealthCheck(
    IInstantQuoteObjectStorageReadinessProbe storage,
    IInstantQuoteScannerReadinessProbe scanner,
    IOptions<InstantQuoteFileOptions> options) : IHealthCheck
{
    private static readonly TimeSpan MaximumProbeDuration = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.OperationTimeout < MaximumProbeDuration
            ? settings.OperationTimeout
            : MaximumProbeDuration);

        try
        {
            await storage.CheckBucketAsync(settings.TemporaryBucket, timeout.Token);
            await storage.CheckBucketAsync(settings.FinalBucket, timeout.Token);
            await scanner.CheckAsync(timeout.Token);
            return HealthCheckResult.Healthy("dependencies available");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("dependency unavailable");
        }
    }
}
