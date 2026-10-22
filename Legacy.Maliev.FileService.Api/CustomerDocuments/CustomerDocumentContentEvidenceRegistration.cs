using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Legacy.Maliev.FileService.Api.CustomerDocuments;

/// <summary>Explicitly composes registry content evidence with the registered protected storage boundary.</summary>
public static class CustomerDocumentContentEvidenceRegistration
{
    /// <summary>Call after registry and protected storage registration; preserves both disabled defaults.</summary>
    /// <remarks>Optional order: AddCustomerDocuments, AddProtectedCustomerDocuments, then this method.
    /// This method does not activate the runtime, select a provider reader or establish owner acceptance.</remarks>
    public static IServiceCollection AddProtectedCustomerDocumentContentEvidence(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<ICustomerDocumentContentEvidence, CustomerDocumentContentEvidenceClient>());
        return services;
    }
}
