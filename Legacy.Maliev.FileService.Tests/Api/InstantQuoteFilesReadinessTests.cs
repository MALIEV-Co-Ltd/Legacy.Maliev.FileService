using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.Null(provider.GetService<IInstantQuoteObjectStorageReadinessProbe>());
        Assert.Null(provider.GetService<IInstantQuoteScannerReadinessProbe>());
    }

    [Fact]
    public async Task Readiness_EnabledWrites_ChecksBothBucketsAndScannerExactlyOnce()
    {
        var storage = new RecordingStorageReadinessProbe();
        var scanner = new RecordingScannerReadinessProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstantQuoteObjectStorageReadinessProbe>(storage);
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(scanner);
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"));

        Assert.Equal(HealthStatus.Healthy, report.Entries["instant_quote_files"].Status);
        Assert.Equal(["quote-final-local", "quote-temp-local"], storage.Buckets.Order().ToArray());
        Assert.Equal(1, scanner.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Readiness_EnabledDependencyUnavailable_IsUnhealthy(
        bool storageUnavailable,
        bool scannerUnavailable)
    {
        var storage = new RecordingStorageReadinessProbe(storageUnavailable);
        var scanner = new RecordingScannerReadinessProbe(scannerUnavailable);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstantQuoteObjectStorageReadinessProbe>(storage);
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
        services.AddSingleton<IInstantQuoteObjectStorageReadinessProbe, UnexpectedFailureStorageProbe>();
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(new RecordingScannerReadinessProbe());
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
        var storage = new RecordingStorageReadinessProbe(hang: true);
        var scanner = new RecordingScannerReadinessProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstantQuoteObjectStorageReadinessProbe>(storage);
        services.AddSingleton<IInstantQuoteScannerReadinessProbe>(scanner);
        services.AddFileServiceRuntime(BuildConfiguration(EnabledConfiguration));
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Tags.Contains("ready"),
            cancellation.Token);

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["instant_quote_files"].Status);
        Assert.True(storage.CancellationObserved);
        Assert.Equal(0, scanner.Calls);
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

    private sealed class RecordingStorageReadinessProbe(bool unavailable = false, bool hang = false)
        : IInstantQuoteObjectStorageReadinessProbe
    {
        public List<string> Buckets { get; } = [];
        public bool CancellationObserved { get; private set; }

        public async Task CheckBucketAsync(string bucket, CancellationToken cancellationToken)
        {
            Buckets.Add(bucket);
            if (unavailable)
            {
                throw new IOException("storage unavailable");
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

    private sealed class RecordingScannerReadinessProbe(bool unavailable = false)
        : IInstantQuoteScannerReadinessProbe
    {
        public int Calls { get; private set; }

        public Task CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return unavailable
                ? Task.FromException(new IOException("scanner unavailable"))
                : Task.CompletedTask;
        }
    }

    private sealed class UnexpectedFailureStorageProbe : IInstantQuoteObjectStorageReadinessProbe
    {
        public Task CheckBucketAsync(string bucket, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive provider detail");
    }
}
