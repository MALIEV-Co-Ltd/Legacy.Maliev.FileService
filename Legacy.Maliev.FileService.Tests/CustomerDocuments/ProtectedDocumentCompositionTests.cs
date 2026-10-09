using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;
using System.Data.Common;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class ProtectedDocumentCompositionTests
{
    [Fact]
    public async Task ActualCompleteModuleCompositionStaysDisabledWithoutOwnerOrDatabaseWork()
    {
        var ownerCalls = 0;
        var database = new RejectDatabaseOpen();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StorageClient>(_ => { ownerCalls++; throw new InvalidOperationException("No owner credentials in disabled composition."); });
        services.AddSingleton<IObjectStorage>(_ => { ownerCalls++; throw new InvalidOperationException("No owner storage in disabled composition."); });
        services.AddCustomerDocuments("Host=127.0.0.1;Port=1;Database=synthetic;Username=synthetic;Timeout=1", enabled: false);
        services.AddDbContext<CustomerDocumentDbContext>(options => options.AddInterceptors(database));
        services.AddProtectedCustomerDocuments();
        services.AddProtectedCustomerDocumentGoogleCloudGenerationReader();
        services.AddProtectedCustomerDocumentContentEvidence();
        services.AddCustomerDocumentNda();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var actor = new DocumentActor(new ClaimsPrincipal());
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.False(provider.GetRequiredService<CustomerDocumentRegistryOptions>().Enabled);
        Assert.False(provider.GetRequiredService<NdaOptions>().Enabled);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable,
            (await scope.ServiceProvider.GetRequiredService<ICustomerDocumentAuthority>().AuthorizeAsync(actor, 23, CustomerDocumentPermissions.Read, default)).Outcome);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<ICustomerDocumentRegistry>().ReadReceiptAsync(actor, 23, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, await scope.ServiceProvider.GetRequiredService<ICustomerDocumentContentEvidence>().ValidateAsync(Guid.NewGuid(), new string('a', 64), default));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<NdaReminderScheduler>().QueueDueAsync(default));
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<IProtectedDocumentGenerationReader>().ReadAsync("synthetic-private", "customer-documents/23/original", 71, 10, default));
        Assert.Equal(0, ownerCalls);
        Assert.Equal(0, database.Attempts);
    }

    [Fact]
    public async Task DisabledReaderCompositionNeverTouchesOwnerCredentialFactory()
    {
        var credentialCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton<StorageClient>(_ => { credentialCalls++; throw new InvalidOperationException("Synthetic owner credentials must remain untouched."); });
        services.AddProtectedCustomerDocuments();
        services.AddProtectedCustomerDocumentGoogleCloudGenerationReader();
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IProtectedDocumentGenerationReader>();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => reader.ReadAsync("synthetic-private", "customer-documents/23/original", 71, 10, default));
        Assert.Equal(0, credentialCalls);
    }

    [Fact]
    public async Task DisabledStorageCompositionNeverTouchesOwnerProviderFactory()
    {
        var providerCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IObjectStorage>(_ => { providerCalls++; throw new InvalidOperationException("Synthetic owner provider must remain untouched."); });
        services.AddProtectedCustomerDocuments();
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IProtectedDocumentStorage>();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => storage.ReadAsync(new("synthetic-private", "customer-documents/23/original", 71, 70, Guid.NewGuid(), 3, new string('b', 64), "application/pdf", "synthetic.pdf"), default));
        Assert.Equal(0, providerCalls);
    }

    private sealed class RejectDatabaseOpen : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("Disabled module attempted database work.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Disabled module attempted database work.");
        }
    }
}
