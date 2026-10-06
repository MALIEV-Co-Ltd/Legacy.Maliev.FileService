using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Tests.Api;

// Controlled admission/registration and real offline SDK signing; no independent resource proof or engine acceptance.
public sealed class HostedFinancialCompletionProfileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Lease = "c821-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static HostedAcceptanceRunContext Context => new(true, "123", 1, Source);

    [Theory]
    [InlineData("schema")]
    [InlineData("run")]
    [InlineData("run-leading-zero")]
    [InlineData("attempt")]
    [InlineData("source")]
    [InlineData("source-case")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("overlong")]
    [InlineData("offset")]
    [InlineData("lease")]
    [InlineData("scanner-host")]
    [InlineData("scanner-port")]
    [InlineData("scanner-container")]
    [InlineData("scanner-image")]
    [InlineData("database-missing")]
    [InlineData("database-stale")]
    [InlineData("database-future")]
    [InlineData("database-files")]
    [InlineData("database-receipt")]
    public void RejectsMismatchedRunSourceLeaseOrScannerEvidence(string mutation)
    {
        var valid = Admission();
        var changed = mutation switch
        {
            "schema" => valid with { SchemaVersion = 2 },
            "run" => valid with { RunId = "124" },
            "run-leading-zero" => valid with { RunId = "0123" },
            "attempt" => valid with { RunAttempt = 2 },
            "source" => valid with { FileSourceSha = new string('b', 40) },
            "source-case" => valid with { FileSourceSha = Source.ToUpperInvariant() },
            "future" => valid with { IssuedUtc = Now.AddSeconds(1) },
            "expired" => valid with { ExpiresUtc = Now },
            "overlong" => valid with { ExpiresUtc = Now.AddMinutes(31) },
            "offset" => valid with { IssuedUtc = Now.ToOffset(TimeSpan.FromHours(7)) },
            "lease" => valid with { ResourceLeaseId = "c821-00000000-0000-0000-0000-000000000000" },
            "scanner-host" => valid with { ScannerHost = "localhost" },
            "scanner-port" => valid with { ScannerPort = 0 },
            "scanner-container" => valid with { ScannerContainerId = "not-an-observed-container" },
            "scanner-image" => valid with { ScannerImageDigest = "clamav:latest" },
            "database-missing" => valid with { ScannerDatabaseIdentity = null },
            "database-stale" => valid with { ScannerDatabaseIdentity = valid.ScannerDatabaseIdentity! with { ObservedUtc = Now.AddMinutes(-6) } },
            "database-future" => valid with { ScannerDatabaseIdentity = valid.ScannerDatabaseIdentity! with { ObservedUtc = Now.AddSeconds(1) } },
            "database-files" => valid with { ScannerDatabaseIdentity = valid.ScannerDatabaseIdentity! with { DatabaseFilesSha256 = [] } },
            "database-receipt" => valid with { ScannerDatabaseIdentity = valid.ScannerDatabaseIdentity! with { ReadinessReceiptSha256 = "missing" } },
            _ => throw new InvalidOperationException(),
        };
        Assert.Throws<InvalidOperationException>(() => HostedFinancialCompletionProfile.ValidateAdmission(changed, Context, Now));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("kind")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("exe")]
    [InlineData("exe-hash")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("ambiguous")]
    public void RejectsIncompleteOrEscapedEndpointIdentity(string mutation)
    {
        var valid = Admission();
        var current = valid.StorageEndpointIdentity!;
        HostedStorageEndpointIdentity? endpoint = mutation switch
        {
            "missing" => null,
            "kind" => current with { Kind = "copied-claim" },
            "pid" => current with { Pid = 0 },
            "start" => current with { StartedUtc = Now.AddSeconds(1) },
            "exe" => current with { ExecutableAbsolutePath = "relative.dll" },
            "exe-hash" => current with { ExecutableSha256 = "bad" },
            "host" => current with { HostIp = "::1" },
            "port" => current with { HostPort = 5011 },
            "ambiguous" => current with { ContainerId = new string('a', 64) },
            _ => throw new InvalidOperationException(),
        };
        Assert.Throws<InvalidOperationException>(() => HostedFinancialCompletionProfile.ValidateAdmission(
            valid with { StorageEndpointIdentity = endpoint }, Context, Now));
    }

    [Fact]
    public void AdmitsTypedProcessAndContainerClaimsWithoutCallingThemObservedProof()
    {
        var valid = Admission();
        HostedFinancialCompletionProfile.ValidateAdmission(valid, Context, Now);
        HostedFinancialCompletionProfile.ValidateAdmission(valid with
        {
            StorageEndpointIdentity = new HostedStorageEndpointIdentity
            {
                Kind = "container",
                ContainerId = new string('a', 64),
                ImageDigest = "sha256:" + new string('b', 64),
                CreatedUtc = Now.AddMinutes(-1),
                HostIp = "127.0.0.1",
                HostPort = 5010,
                ContainerPort = 4443,
                OwnershipLabels = new() { ["fixture"] = Lease },
            },
        }, Context, Now);
        Assert.Throws<InvalidOperationException>(() => HostedFinancialCompletionProfile.ValidateAdmission(valid, Context with { HostedLinux = false }, Now));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void OrdinaryEnvironmentRejectsProfileSelection(string environment)
    {
        var builder = Builder(environment);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HostedFinancialCompletionAcceptance:Enabled"] = "true" });
        Assert.Throws<InvalidOperationException>(() => builder.AddHostedFinancialCompletionAcceptance());
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(HostedAcceptanceSigningIdentity));
    }

    [Fact]
    public void DedicatedEnvironmentRejectsMissingAdmission()
    {
        var builder = Builder(HostedFinancialCompletionProfile.EnvironmentName);
        Assert.Throws<InvalidOperationException>(() => builder.AddHostedFinancialCompletionAcceptance());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ActualHostedRegistrationUsesSdkOfflineV4SignerAndRealScannerTypes(string host)
    {
        var builder = Builder(HostedFinancialCompletionProfile.EnvironmentName);
        var valid = Admission() with
        {
            RunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? string.Empty,
            RunAttempt = int.Parse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
            FileSourceSha = HostedFinancialCompletionProfile.BuildSourceSha(),
            StorageOrigin = $"http://{(host == "::1" ? "[::1]" : host)}:5010/",
            ScannerHost = host,
            StorageEndpointIdentity = Admission().StorageEndpointIdentity! with { HostIp = host },
        };
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            HostedFinancialCompletionAcceptance = new { Enabled = true, Admission = valid },
            FileStorage = new { Enabled = true, WritesEnabled = true, AllowedBuckets = new[] { "synthetic-private" } },
            InstantQuoteFiles = new { Enabled = false },
        })));
        builder.Configuration.AddJsonStream(json);
        var clock = new FixedClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.AddHostedFinancialCompletionAcceptance();
        builder.Services.AddFileServiceRuntime(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = provider.GetRequiredService<StorageClient>();
        Assert.Same(client, provider.GetRequiredService<StorageClient>());
        Assert.IsType<ClamAvFileSafetyScanner>(scope.ServiceProvider.GetRequiredService<IFileSafetyScanner>());
        var scanner = provider.GetRequiredService<IOptions<MalwareScannerOptions>>().Value;
        Assert.Equal((host, 3310, 10), (scanner.Host, scanner.Port, scanner.TimeoutSeconds));
        var adapter = new GoogleCloudObjectStorage(client, provider.GetRequiredService<UrlSigner>(), journal: null, hostedOrigin:
            provider.GetRequiredService<HostedAcceptanceSignedReadOrigin>());
        var uri = await adapter.CreateSignedGenerationReadUriAsync("synthetic-private", "invoices/part.pdf", 17, TimeSpan.FromDays(10), default);
        Assert.Equal(("http", host == "::1" ? "[::1]" : host, 5010), (uri.Scheme, uri.Host, uri.Port));
        var query = uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.Equal("17", query["generation"]);
        Assert.Equal("604800", query["X-Goog-Expires"]);
        Assert.Equal(512, query["X-Goog-Signature"].Length);
        var publicKey = provider.GetRequiredService<HostedAcceptanceSigningIdentity>().VerificationPublicKey;
        Assert.NotEmpty(publicKey);
        Assert.True(VerifySignature(uri, publicKey));
        Assert.False(VerifySignature(new Uri(uri.AbsoluteUri.Replace("generation=17", "generation=18", StringComparison.Ordinal)), publicKey));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<HostedAcceptanceSignedReadOrigin>()
            .ValidateSignedUri("https://storage.googleapis.com/escaped"));
        clock.Current = Now.AddMinutes(11);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetObjectAsync("synthetic-private", "invoices/part.pdf"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.CreateSignedGenerationReadUriAsync(
            "synthetic-private", "invoices/part.pdf", 17, TimeSpan.FromHours(1), default));
    }

    private static WebApplicationBuilder Builder(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        return builder;
    }

    private static HostedFinancialCompletionAdmission Admission() => new()
    {
        SchemaVersion = 1,
        RunId = "123",
        RunAttempt = 1,
        FileSourceSha = Source,
        IssuedUtc = Now,
        ExpiresUtc = Now.AddMinutes(10),
        ResourceLeaseId = Lease,
        StorageOrigin = "http://127.0.0.1:5010/",
        ScannerHost = "127.0.0.1",
        ScannerPort = 3310,
        ScannerContainerId = new string('b', 64),
        ScannerImageDigest = "sha256:" + new string('c', 64),
        StorageEndpointIdentity = new HostedStorageEndpointIdentity
        {
            Kind = "process",
            Pid = 123,
            StartedUtc = Now.AddMinutes(-1),
            ExecutableAbsolutePath = "/usr/bin/dotnet",
            ExecutableSha256 = new string('d', 64),
            HostIp = "127.0.0.1",
            HostPort = 5010,
        },
        ScannerDatabaseIdentity = new HostedScannerDatabaseIdentity
        {
            EngineVersion = "synthetic-engine-version",
            LoadedDatabaseVersion = "synthetic-database-version",
            ObservedUtc = Now,
            ReadinessReceiptSha256 = new string('e', 64),
            DatabaseFilesSha256 = new() { ["daily.cvd"] = new string('f', 64) },
        },
    };

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private static bool VerifySignature(Uri uri, string publicKey)
    {
        var pairs = uri.Query.TrimStart('?').Split('&');
        var query = pairs.Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        var canonicalQuery = string.Join('&', pairs.Where(pair => !pair.StartsWith("X-Goog-Signature=", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        // SDK Options.Port affects the resulting URL but is not included in the signature; transport independently binds port.
        var canonical = $"GET\n{uri.AbsolutePath}\n{canonicalQuery}\nhost:{uri.Host}\n\nhost\nUNSIGNED-PAYLOAD";
        var scope = query["X-Goog-Credential"].Split('/', 2)[1];
        var payload = $"GOOG4-RSA-SHA256\n{query["X-Goog-Date"]}\n{scope}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return rsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromHexString(query["X-Goog-Signature"]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
