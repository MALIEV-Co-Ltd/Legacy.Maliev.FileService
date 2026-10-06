using System.Reflection;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;
using Google.Api.Gax;
using Google.Cloud.Storage.V1;

namespace Legacy.Maliev.FileService.Api;

/// <summary>Registers the dedicated hosted financial acceptance composition after finite admission.</summary>
public static class HostedFinancialCompletionProfile
{
    /// <summary>The only environment in which isolated hosted storage may be selected.</summary>
    public const string EnvironmentName = "HostedFinancialCompletionAcceptance";

    /// <summary>Admits hosted SDK/scanner dependencies before ordinary runtime registration.</summary>
    public static WebApplicationBuilder AddHostedFinancialCompletionAcceptance(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var section = builder.Configuration.GetSection(EnvironmentName);
        var selected = builder.Environment.IsEnvironment(EnvironmentName);
        if (!selected && !section.Exists()) return builder;
        if (!selected || !section.GetValue<bool>("Enabled"))
            throw new InvalidOperationException("Hosted acceptance requires its explicit dedicated environment and enabled admission.");

        var admission = section.GetSection("Admission").Get<HostedFinancialCompletionAdmission>(options => options.ErrorOnUnknownConfiguration = true)
            ?? throw new InvalidOperationException("Hosted acceptance admission is absent.");
        var context = new HostedAcceptanceRunContext(
            IsHostedLinux(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")),
            Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? string.Empty,
            int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), out var attempt) ? attempt : 0,
            BuildSourceSha());
        var clock = builder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(TimeProvider))?.ImplementationInstance as TimeProvider
            ?? TimeProvider.System;
        ValidateAdmission(admission, context, clock.GetUtcNow());
        var origin = new Uri(admission.StorageOrigin, UriKind.Absolute);
        _ = new HostedAcceptanceStorageTransport(origin, admission.ExpiresUtc, clock);

        if (!builder.Configuration.GetValue<bool>("FileStorage:Enabled") ||
            !builder.Configuration.GetValue<bool>("FileStorage:WritesEnabled") ||
            builder.Configuration.GetValue<bool>("InstantQuoteFiles:Enabled"))
            throw new InvalidOperationException("Hosted financial acceptance requires the legacy upload workflow and excludes other storage workflows.");
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(StorageClient) ||
            descriptor.ServiceType == typeof(UrlSigner)))
            throw new InvalidOperationException("Hosted acceptance cannot replace an existing storage client or signer.");

        builder.Services.AddSingleton(admission);
        builder.Services.AddSingleton(provider => new HostedAcceptanceStorageTransport(origin, admission.ExpiresUtc,
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(provider => new StorageClientBuilder
        {
            BaseUri = origin.AbsoluteUri,
            UnauthenticatedAccess = true,
            EmulatorDetection = EmulatorDetection.None,
            HttpClientFactory = provider.GetRequiredService<HostedAcceptanceStorageTransport>(),
        }.Build());
        builder.Services.AddSingleton<HostedAcceptanceSigningIdentity>();
        builder.Services.AddSingleton(provider => provider.GetRequiredService<HostedAcceptanceSigningIdentity>().Signer);
        builder.Services.AddSingleton(provider => new HostedAcceptanceSignedReadOrigin(origin, admission.ExpiresUtc,
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(provider => new HostedAcceptanceDependencyLease(admission.ExpiresUtc,
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.PostConfigure<MalwareScannerOptions>(options =>
        {
            options.Host = admission.ScannerHost;
            options.Port = admission.ScannerPort;
            options.TimeoutSeconds = 10;
        });
        return builder;
    }

    // The pinned validation action overrides GITHUB_ACTIONS for local project references.
    // GitHub's runner classification still distinguishes hosted from self-hosted runners.
    internal static bool IsHostedLinux(bool linux, string? runnerEnvironment) => linux && runnerEnvironment == "github-hosted";

    internal static string BuildSourceSha()
    {
        var version = typeof(HostedFinancialCompletionProfile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var candidate = version?.Split('+').Last();
        return candidate is not null && IsHex(candidate, 40) ? candidate : string.Empty;
    }

    internal static void ValidateAdmission(HostedFinancialCompletionAdmission admission, HostedAcceptanceRunContext context, DateTimeOffset now)
    {
        if (!context.HostedLinux || admission.SchemaVersion != 1 ||
            !IsPositiveDecimal(admission.RunId) || admission.RunId != context.RunId ||
            admission.RunAttempt <= 0 || admission.RunAttempt != context.RunAttempt ||
            !IsHex(admission.FileSourceSha, 40) || admission.FileSourceSha != context.FileSourceSha ||
            admission.IssuedUtc.Offset != TimeSpan.Zero || admission.ExpiresUtc.Offset != TimeSpan.Zero ||
            admission.IssuedUtc > now || admission.ExpiresUtc <= now || admission.IssuedUtc >= admission.ExpiresUtc ||
            admission.ExpiresUtc - admission.IssuedUtc > TimeSpan.FromMinutes(30) ||
            !IsResourceLease(admission.ResourceLeaseId) ||
            admission.ScannerHost is not ("127.0.0.1" or "::1") || admission.ScannerPort is <= 0 or > 65535 ||
            !IsHex(admission.ScannerContainerId, 64) ||
            !IsDigest(admission.ScannerImageDigest) || !IsDatabaseIdentity(admission.ScannerDatabaseIdentity, now))
            throw new InvalidOperationException("Hosted acceptance admission does not match the current source, run, resources or finite lease.");
        var origin = new Uri(admission.StorageOrigin, UriKind.Absolute);
        _ = new HostedAcceptanceStorageTransport(origin, admission.ExpiresUtc, new AdmissionClock(now));
        if (admission.ScannerHost != origin.Host.Trim('[', ']') ||
            !IsEndpointIdentity(admission.StorageEndpointIdentity, admission, origin, now))
            throw new InvalidOperationException("Hosted acceptance resource identities differ from the admitted loopback endpoints.");
    }

    private static bool IsHex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsPositiveDecimal(string? value) => long.TryParse(value, out var number) && number > 0 &&
        value == number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool IsDigest(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex(value[7..], 64);
    private static bool IsResourceLease(string? value) => value is not null && value.StartsWith("c821-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value[5..], "D", out var lease) && lease != Guid.Empty && value == "c821-" + lease.ToString("D");

    private static bool IsDatabaseIdentity(HostedScannerDatabaseIdentity? database, DateTimeOffset now) =>
        database is not null && !string.IsNullOrWhiteSpace(database.EngineVersion) && database.EngineVersion.Length <= 128 &&
        !string.IsNullOrWhiteSpace(database.LoadedDatabaseVersion) && database.LoadedDatabaseVersion.Length <= 128 &&
        database.ObservedUtc.Offset == TimeSpan.Zero && database.ObservedUtc <= now && now - database.ObservedUtc <= TimeSpan.FromMinutes(5) &&
        IsHex(database.ReadinessReceiptSha256, 64) && database.DatabaseFilesSha256 is { Count: > 0 and <= 16 } &&
        database.DatabaseFilesSha256.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 &&
            !pair.Key.Contains('/') && !pair.Key.Contains('\\') && IsHex(pair.Value, 64));

    private static bool IsEndpointIdentity(HostedStorageEndpointIdentity? endpoint, HostedFinancialCompletionAdmission admission, Uri origin, DateTimeOffset now)
    {
        if (endpoint is null || endpoint.HostIp != origin.Host.Trim('[', ']') || endpoint.HostPort != origin.Port) return false;
        return endpoint.Kind switch
        {
            "process" => endpoint.Pid > 0 && endpoint.StartedUtc.Offset == TimeSpan.Zero && endpoint.StartedUtc <= now &&
                !string.IsNullOrWhiteSpace(endpoint.ExecutableAbsolutePath) && Path.IsPathFullyQualified(endpoint.ExecutableAbsolutePath) &&
                IsHex(endpoint.ExecutableSha256, 64) && string.IsNullOrEmpty(endpoint.ContainerId) && string.IsNullOrEmpty(endpoint.ImageDigest) &&
                endpoint.OwnershipLabels is { Count: 0 } && endpoint.ContainerPort == 0 && endpoint.CreatedUtc == default,
            "container" => IsHex(endpoint.ContainerId, 64) && IsDigest(endpoint.ImageDigest) && endpoint.CreatedUtc.Offset == TimeSpan.Zero &&
                endpoint.CreatedUtc <= now && endpoint.ContainerPort is > 0 and <= 65535 && endpoint.Pid == 0 &&
                string.IsNullOrEmpty(endpoint.ExecutableAbsolutePath) && string.IsNullOrEmpty(endpoint.ExecutableSha256) && endpoint.StartedUtc == default &&
                endpoint.OwnershipLabels is { Count: > 0 and <= 32 } && endpoint.OwnershipLabels.Values.Contains(admission.ResourceLeaseId, StringComparer.Ordinal) &&
                endpoint.OwnershipLabels.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 && pair.Value is { Length: > 0 and <= 256 }),
            _ => false,
        };
    }
    private sealed class AdmissionClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed record HostedAcceptanceRunContext(bool HostedLinux, string RunId, int RunAttempt, string FileSourceSha);

internal sealed record HostedFinancialCompletionAdmission
{
    public HostedFinancialCompletionAdmission() { }
    public int SchemaVersion { get; init; }
    public string RunId { get; init; } = string.Empty;
    public int RunAttempt { get; init; }
    public string FileSourceSha { get; init; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string StorageOrigin { get; init; } = string.Empty;
    public HostedStorageEndpointIdentity? StorageEndpointIdentity { get; init; }
    public string ScannerHost { get; init; } = string.Empty;
    public int ScannerPort { get; init; }
    public string ScannerContainerId { get; init; } = string.Empty;
    public string ScannerImageDigest { get; init; } = string.Empty;
    public HostedScannerDatabaseIdentity? ScannerDatabaseIdentity { get; init; }
    public string ResourceLeaseId { get; init; } = string.Empty;
}

internal sealed record HostedStorageEndpointIdentity
{
    public HostedStorageEndpointIdentity() { }
    public string Kind { get; init; } = string.Empty;
    public string HostIp { get; init; } = string.Empty;
    public int HostPort { get; init; }
    public int Pid { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public string ExecutableAbsolutePath { get; init; } = string.Empty;
    public string ExecutableSha256 { get; init; } = string.Empty;
    public string ContainerId { get; init; } = string.Empty;
    public string ImageDigest { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public Dictionary<string, string> OwnershipLabels { get; init; } = [];
    public int ContainerPort { get; init; }
}

internal sealed record HostedScannerDatabaseIdentity
{
    public HostedScannerDatabaseIdentity() { }
    public string EngineVersion { get; init; } = string.Empty;
    public string LoadedDatabaseVersion { get; init; } = string.Empty;
    public Dictionary<string, string> DatabaseFilesSha256 { get; init; } = [];
    public DateTimeOffset ObservedUtc { get; init; }
    public string ReadinessReceiptSha256 { get; init; } = string.Empty;
}
