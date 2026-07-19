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
                options => !options.WritesEnabled || options.AllowedBuckets.Length > 0,
                "At least one allowed bucket is required when legacy writes are enabled")
            .Validate(
                options => !options.WritesEnabled || options.SignedUrlHours is >= 1 and <= 168,
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

        var legacyWritesEnabled = configuration.GetValue<bool>($"{FileStorageOptions.SectionName}:WritesEnabled");
        var instantQuoteEnabled = configuration.GetValue<bool>($"{InstantQuoteFileOptions.SectionName}:Enabled") &&
            configuration.GetValue<bool>($"{InstantQuoteFileOptions.SectionName}:WritesEnabled");

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
        services.TryAddScoped<ObjectNamePolicy>();
        services.TryAddScoped<IFileService, FileApplicationService>();
        services.TryAddScoped<IInstantQuoteFileService, InstantQuoteFileService>();
        services.TryAddScoped<IdempotentUploadCoordinator>();
        services.TryAddSingleton<IInstantQuoteMultipartReader, SingleFileMultipartReader>();
        return services;
    }

    private static bool ValidateInstantQuoteOptions(InstantQuoteFileOptions options)
    {
        if (!options.Enabled)
        {
            return true;
        }

        if (options.SessionLifetime < TimeSpan.FromMinutes(5) ||
            options.SessionLifetime > TimeSpan.FromDays(7) ||
            options.CleanupTimeout < TimeSpan.FromSeconds(1) ||
            options.CleanupTimeout > TimeSpan.FromMinutes(5))
        {
            return false;
        }

        return !options.WritesEnabled ||
            IsBucketName(options.TemporaryBucket) &&
            IsBucketName(options.FinalBucket) &&
            !string.Equals(options.TemporaryBucket, options.FinalBucket, StringComparison.Ordinal);
    }

    private static bool IsBucketName(string value) =>
        value.Length is >= 3 and <= 63 &&
        value[0] is not ('.' or '-') &&
        value[^1] is not ('.' or '-') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
