using System.Net;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Legacy.Maliev.FileService.Api;

/// <summary>Registers storage workflows with explicit, fail-closed runtime write gates.</summary>
public static class FileServiceRuntimeRegistration
{
    /// <summary>Registers legacy and instant-quotation file workflows for the supplied configuration.</summary>
    public static IServiceCollection AddFileServiceRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .Validate(
                options => !IsLegacyWriteEnabled(options) || options.AllowedBuckets.Length > 0,
                "At least one allowed bucket is required when legacy writes are enabled")
            .Validate(
                options => !IsLegacyWriteEnabled(options) || options.SignedUrlHours is >= 1 and <= 168,
                "Signed URL lifetime must be between one hour and seven days")
            .ValidateOnStart();
        services.AddOptions<InstantQuoteFileOptions>()
            .Bind(configuration.GetSection(InstantQuoteFileOptions.SectionName))
            .Validate(ValidateInstantQuoteOptions, "Enabled instant-quotation file settings are unsafe")
            .ValidateOnStart();
        services.AddOptions<MalwareScannerOptions>()
            .Bind(configuration.GetSection(MalwareScannerOptions.SectionName))
            .Validate(options => options.Port is > 0 and <= 65535, "Scanner port is invalid")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 300, "Scanner timeout is invalid")
            .ValidateOnStart();

        var legacyWritesEnabled = configuration.GetValue<bool>($"{FileStorageOptions.SectionName}:Enabled") &&
            configuration.GetValue<bool>($"{FileStorageOptions.SectionName}:WritesEnabled");
        var instantQuoteEnabled = configuration.GetValue<bool>($"{InstantQuoteFileOptions.SectionName}:Enabled") &&
            configuration.GetValue<bool>($"{InstantQuoteFileOptions.SectionName}:WritesEnabled");
        var instantQuoteCleanupEnabled = instantQuoteEnabled &&
            configuration.GetValue<bool>($"{InstantQuoteFileOptions.SectionName}:CleanupEnabled");

        if (legacyWritesEnabled || instantQuoteEnabled)
        {
            services.TryAddSingleton(_ => StorageClient.Create());
        }

        if (legacyWritesEnabled)
        {
            services.TryAddSingleton(provider => provider.GetRequiredService<StorageClient>().CreateUrlSigner());
            services.TryAddScoped<IObjectStorage, GoogleCloudObjectStorage>();
            services.TryAddScoped<IFileSafetyScanner, ClamAvFileSafetyScanner>();
        }
        else
        {
            services.TryAddScoped<IObjectStorage, DisabledObjectStorage>();
            services.TryAddScoped<IFileSafetyScanner, DisabledFileSafetyScanner>();
        }

        if (instantQuoteEnabled)
        {
            services.TryAddScoped<IInstantQuoteObjectStorage, InstantQuoteGoogleCloudObjectStorage>();
            services.TryAddScoped<IInstantQuoteFileSafetyScanner, ClamAvFileSafetyScanner>();
        }
        else
        {
            services.TryAddScoped<IInstantQuoteObjectStorage, DisabledInstantQuoteObjectStorage>();
            services.TryAddScoped<IInstantQuoteFileSafetyScanner, DisabledInstantQuoteFileSafetyScanner>();
        }

        services.TryAddScoped<IUploadRepository, UploadRepository>();
        services.TryAddScoped<IUploadIdempotencyStore, RedisUploadIdempotencyStore>();
        services.TryAddScoped<IInstantQuoteFileRepository, InstantQuoteFileRepository>();
        services.TryAddScoped<IInstantQuoteCleanupRepository, InstantQuoteFileRepository>();
        services.TryAddScoped<ObjectNamePolicy>();
        services.TryAddSingleton<LegacyFileRuntimeGate>();
        services.TryAddScoped<IFileService, FileApplicationService>();
        services.TryAddScoped<IInstantQuoteFileService, InstantQuoteFileService>();
        services.TryAddScoped<InstantQuoteTemporaryObjectCleanupService>();
        services.TryAddScoped<IdempotentUploadCoordinator>();
        services.TryAddSingleton<IInstantQuoteMultipartReader, SingleFileMultipartReader>();
        if (instantQuoteCleanupEnabled)
        {
            services.AddHostedService<InstantQuoteTemporaryObjectCleanupHostedService>();
        }
        return services;
    }

    private static bool ValidateInstantQuoteOptions(InstantQuoteFileOptions options)
    {
        if (!options.Enabled)
        {
            return !options.CleanupEnabled;
        }

        if (options.SessionLifetime < TimeSpan.FromMinutes(5) ||
            options.SessionLifetime > TimeSpan.FromDays(7) ||
            options.CleanupTimeout < TimeSpan.FromSeconds(1) ||
            options.CleanupTimeout > TimeSpan.FromMinutes(5) ||
            options.OperationLeaseTimeout <= options.CleanupTimeout ||
            options.OperationLeaseTimeout > TimeSpan.FromDays(1))
        {
            return false;
        }

        if (options.CleanupEnabled &&
            (!options.WritesEnabled ||
             options.CleanupInterval < TimeSpan.FromSeconds(10) ||
             options.CleanupInterval > TimeSpan.FromDays(1) ||
             options.CleanupRetryDelay < TimeSpan.FromSeconds(10) ||
             options.CleanupRetryDelay > TimeSpan.FromDays(1) ||
             options.CleanupRetryDelay < options.CleanupTimeout.Add(TimeSpan.FromSeconds(5)) ||
             options.CleanupSessionExpiryGrace < TimeSpan.Zero ||
             options.CleanupSessionExpiryGrace > TimeSpan.FromDays(7) ||
             options.CleanupBatchSize is < 1 or > 500))
        {
            return false;
        }

        return !options.WritesEnabled ||
            IsBucketName(options.TemporaryBucket) &&
            IsBucketName(options.FinalBucket) &&
            !string.Equals(options.TemporaryBucket, options.FinalBucket, StringComparison.Ordinal);
    }

    private static bool IsLegacyWriteEnabled(FileStorageOptions options) =>
        options.Enabled && options.WritesEnabled;

    private static bool IsBucketName(string value)
    {
        if (value.Length is < 3 or > 222 ||
            value.Count(character => character == '.') == 3 && IPAddress.TryParse(value, out _) ||
            value.StartsWith("goog", StringComparison.Ordinal) ||
            value.Contains("google", StringComparison.Ordinal) ||
            value.Contains("g00gle", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = value.Split('.');
        return labels.All(label =>
            label.Length is >= 1 and <= 63 &&
            IsLowerAsciiLetterOrDigit(label[0]) &&
            IsLowerAsciiLetterOrDigit(label[^1]) &&
            label.All(character => IsLowerAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
