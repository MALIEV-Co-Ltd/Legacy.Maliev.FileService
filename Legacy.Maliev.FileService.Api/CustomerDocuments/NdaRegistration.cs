using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Legacy.Maliev.FileService.Api.CustomerDocuments;
/// <summary>Registers disabled-by-default NDA boundaries and explicitly opted-in internal daily worklists.</summary>
public static class NdaRegistration
{
    /// <summary>Adds NDA services after registry persistence; accepted owner adapters replace unavailable bindings.</summary>
    public static IServiceCollection AddCustomerDocumentNda(this IServiceCollection services, NdaOptions? options = null)
    {
        services.AddSingleton(options ?? new NdaOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<NdaCanonicalCoverageResolver>();
        services.TryAddScoped<INdaOrderConsentReader, NdaOrderConsentHttpReader>();
        services.TryAddScoped<INdaAuthority, NdaOwnerAuthority>();
        services.TryAddScoped<INdaRepository, NdaRepository>();
        services.AddScoped<NdaVerificationService>();
        services.AddScoped<IDocumentProtectionService, DocumentProtectionService>();
        services.AddScoped<NdaReminderScheduler>();
        services.TryAddScoped<INdaReminderQueue>(provider => provider.GetRequiredService<NdaReminderScheduler>());
        if (options is { Enabled: true, SchedulerEnabled: true }) services.AddHostedService<NdaReminderHostedService>();
        return services;
    }
}
