using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
namespace Legacy.Maliev.FileService.Api.CustomerDocuments;
/// <summary>Registers protected workflows disabled until an explicitly authorized activation.</summary>
public static class ProtectedCustomerDocumentRegistration
{
    /// <summary>Selects the private reader using an existing owner-registered StorageClient; does not activate the feature.</summary>
    public static IServiceCollection AddProtectedCustomerDocumentGoogleCloudGenerationReader(this IServiceCollection services)
    {
        services.AddOptions<CustomerDocumentOptions>();
        services.Replace(ServiceDescriptor.Scoped<IProtectedDocumentGenerationReader>(provider =>
            CanUseStorage(provider) ? ActivatorUtilities.CreateInstance<CustomerDocumentGoogleCloudGenerationReader>(provider) : new UnavailableProtectedDocumentGenerationReader()));
        return services;
    }
    /// <summary>Adds protected services using existing owner-registered authority and storage boundaries.</summary>
    public static IServiceCollection AddProtectedCustomerDocuments(this IServiceCollection services)
    {
        services.AddOptions<CustomerDocumentOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IProtectedDocumentGenerationReader, UnavailableProtectedDocumentGenerationReader>();
        services.TryAddScoped<IProtectedDocumentStore, ProtectedDocumentStore>();
        services.TryAddScoped<IProtectedDocumentStorage>(provider =>
            CanUseStorage(provider) ? ActivatorUtilities.CreateInstance<CustomerDocumentStorageAdapter>(provider) : new UnavailableProtectedDocumentStorage());
        services.TryAddScoped<CustomerDocumentUploadService>();
        services.TryAddScoped<CustomerDocumentDownloadService>();
        return services;
    }
    private static bool CanUseStorage(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<CustomerDocumentOptions>>().Value;
        return options.Enabled && !string.IsNullOrWhiteSpace(options.PrivateBucket);
    }
}
