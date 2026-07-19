using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Legacy.Maliev.FileService.Tests.Api;

public sealed class InstantQuoteFilesReadinessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Readiness_WriteGateDisabled_IsHealthyWithoutResolvingDependencies(
        bool enabled,
        bool writesEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileServiceRuntime(BuildConfiguration(
        [
            new("InstantQuoteFiles:Enabled", enabled.ToString()),
            new("InstantQuoteFiles:WritesEnabled", writesEnabled.ToString()),
        ]));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        var entry = report.Entries["instant_quote_files"];
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal("disabled", entry.Description);
        Assert.Null(provider.GetService<StorageClient>());
        Assert.Null(provider.GetService<IInstantQuoteScannerReadinessProbe>());
    }

    [Fact]
    public async Task Readiness_EnabledWrites_DoesNotResolveStorageClientAndChecksScannerExactlyOnce()
    {
        var storageClientResolutionCalls = 0;
        var scanner = new RecordingScannerReadinessProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StorageClient>(_ =>
        {
            storageClientResolutionCalls++;
            throw new InvalidOperationException("Readiness must not resolve ADC-backed storage.");
        });
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(scanner);
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IInstantQuoteObjectStorage) &&
            descriptor.ImplementationType == typeof(InstantQuoteGoogleCloudObjectStorage));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        Assert.Equal(HealthStatus.Healthy, report.Entries["instant_quote_files"].Status);
        Assert.Equal(0, storageClientResolutionCalls);
        Assert.Equal(1, scanner.Calls);
    }

    [Fact]
    public async Task Readiness_EnabledWrites_MissingStorageAdapterRegistrationIsUnhealthyWithoutResolvingAdc()
    {
        var storageClientResolutionCalls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StorageClient>(_ =>
        {
            storageClientResolutionCalls++;
            throw new InvalidOperationException("Readiness must not resolve ADC-backed storage.");
        });
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(new RecordingScannerReadinessProbe());
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        services.RemoveAll<IInstantQuoteObjectStorage>();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["instant_quote_files"].Status);
        Assert.Equal(0, storageClientResolutionCalls);
    }

    [Fact]
    public async Task Readiness_EnabledWrites_InvalidBucketConfigurationIsUnhealthyWithoutDetailsOrAdc()
    {
        var storageClientResolutionCalls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StorageClient>(_ =>
        {
            storageClientResolutionCalls++;
            throw new InvalidOperationException("Readiness must not resolve ADC-backed storage.");
        });
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(new RecordingScannerReadinessProbe());
        services.AddFileServiceRuntime(BuildConfiguration(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "true"),
            new("InstantQuoteFiles:CleanupEnabled", "true"),
        ]));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        var entry = report.Entries["instant_quote_files"];
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Null(entry.Exception);
        Assert.Equal(0, storageClientResolutionCalls);
    }

    [Fact]
    public async Task Readiness_EnabledScannerUnavailable_IsUnhealthy()
    {
        var scanner = new RecordingScannerReadinessProbe(unavailable: true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(scanner);
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["instant_quote_files"].Status);
    }

    [Fact]
    public async Task Readiness_UnexpectedDependencyFailure_DoesNotExposeExceptionDetails()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstantQuoteScannerReadinessProbe, UnexpectedFailureScannerProbe>();
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        var entry = report.Entries["instant_quote_files"];
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Null(entry.Exception);
        Assert.Equal("dependency unavailable", entry.Description);
    }

    [Fact]
    public async Task Readiness_EnabledProbeHangs_CancelsWithinCallerBound()
    {
        var scanner = new RecordingScannerReadinessProbe(hang: true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(scanner);
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"),
            cancellation.Token);

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["instant_quote_files"].Status);
        Assert.True(scanner.CancellationObserved);
        Assert.Equal(1, scanner.Calls);
    }

    private static readonly KeyValuePair<string, string?>[] EnabledConfiguration =
    [
        new("InstantQuoteFiles:Enabled", "true"),
        new("InstantQuoteFiles:WritesEnabled", "true"),
        new("InstantQuoteFiles:CleanupEnabled", "true"),
        new("InstantQuoteFiles:TemporaryBucket", "quote-temp-local"),
        new("InstantQuoteFiles:FinalBucket", "quote-final-local"),
        new("MalwareScanner:Host", "clamav.local"),
    ];

    private static IConfiguration BuildConfiguration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class RecordingScannerReadinessProbe(bool unavailable = false, bool hang = false)
        : IInstantQuoteScannerReadinessProbe
    {
        public int Calls { get; private set; }
        public bool CancellationObserved { get; private set; }

        public async Task CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (unavailable)
            {
                throw new IOException("scanner unavailable");
            }

            if (hang)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
        }
    }

    private sealed class UnexpectedFailureScannerProbe : IInstantQuoteScannerReadinessProbe
    {
        public Task CheckAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive provider detail");
    }
}
