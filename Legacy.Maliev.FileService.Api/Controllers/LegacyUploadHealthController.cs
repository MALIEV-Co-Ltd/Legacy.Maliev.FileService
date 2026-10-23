using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Legacy.Maliev.FileService.Api.Controllers;

/// <summary>Preserves the original anonymous UploadService probe URLs.</summary>
[ApiController]
[Route("uploads")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class LegacyUploadHealthController(HealthCheckService healthChecks) : ControllerBase
{
    /// <summary>Reports process liveness without contacting dependencies.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The legacy plain-text health status.</returns>
    [Route("liveness")]
    public Task<IActionResult> LivenessAsync(CancellationToken cancellationToken) =>
        ReportAsync(_ => false, cancellationToken);

    /// <summary>Reports readiness under the current all-dependency health policy.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The legacy plain-text health status and current readiness status code.</returns>
    [Route("readiness")]
    public Task<IActionResult> ReadinessAsync(CancellationToken cancellationToken) =>
        ReportAsync(_ => true, cancellationToken);

    private async Task<IActionResult> ReportAsync(Func<HealthCheckRegistration, bool> predicate, CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(predicate, cancellationToken);
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";
        return new ContentResult
        {
            Content = report.Status.ToString(),
            ContentType = "text/plain; charset=utf-8",
            StatusCode = report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK,
        };
    }
}
