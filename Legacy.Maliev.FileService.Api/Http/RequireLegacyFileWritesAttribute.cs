using Legacy.Maliev.FileService.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Legacy.Maliev.FileService.Api.Http;

/// <summary>Requires the legacy runtime and write authorities before request model binding.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireLegacyFileWritesAttribute : TypeFilterAttribute
{
    /// <summary>Creates the pre-model-binding legacy write gate.</summary>
    public RequireLegacyFileWritesAttribute()
        : base(typeof(LegacyFileWritesResourceFilter))
    {
        Order = int.MinValue;
    }
}

internal sealed class LegacyFileWritesResourceFilter(LegacyFileRuntimeGate runtimeGate) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        try
        {
            runtimeGate.EnsureWritesEnabled();
        }
        catch (MalwareScannerUnavailableException)
        {
            context.Result = LegacyFileProblem.Unavailable();
            return;
        }

        await next();
    }
}
