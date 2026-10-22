using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Legacy.Maliev.FileService.Api.CustomerDocuments;
/// <summary>Registers the disabled-by-default module and unavailable owner adapter boundaries.</summary>
public static class CustomerDocumentRegistration
{
    // Additive registration for owner integration. No scheduler, migration or runtime activation.
    /// <summary>Registers isolated registry persistence; runtime readback remains disabled unless explicitly enabled.</summary>
    public static IServiceCollection AddCustomerDocuments(this IServiceCollection services, string connectionString, bool enabled = false)
    {
        services.AddDbContext<CustomerDocumentDbContext>(options => options.UseNpgsql(connectionString,
            postgres => postgres.MigrationsHistoryTable("__CustomerDocumentMigrationsHistory")));
        services.TryAddScoped<ICustomerDocumentAuthority, UnavailableCustomerDocumentAuthority>();
        services.TryAddScoped<ICustomerDocumentAssociationValidator, CustomerDocumentAssociationClient>();
        services.TryAddScoped<ICustomerDocumentContentEvidence, UnavailableContentEvidence>();
        services.AddSingleton(new CustomerDocumentRegistryOptions { Enabled = enabled });
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICustomerDocumentRegistry, CustomerDocumentRegistry>();
        services.TryAddScoped<IDocumentVerificationEmployeeAuthority, UnavailableVerificationEmployeeAuthority>();
        services.AddScoped<IDocumentEvidenceVerificationService, DocumentEvidenceVerificationService>();
        return services;
    }
    private sealed class UnavailableCustomerDocumentAuthority : ICustomerDocumentAuthority
    {
        /// <summary>Provides customer document registry evidence.</summary>
        public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) =>
            Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Unavailable));
    }
    private sealed class UnavailableContentEvidence : ICustomerDocumentContentEvidence
    {
        public Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token) =>
            Task.FromResult(DocumentAuthorityOutcome.Unavailable);
    }
    private sealed class UnavailableVerificationEmployeeAuthority : IDocumentVerificationEmployeeAuthority
    {
        public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) =>
            Task.FromResult(DocumentAuthorityOutcome.Unavailable);
    }
}
