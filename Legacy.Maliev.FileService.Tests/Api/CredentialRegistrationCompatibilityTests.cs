using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.Api;

// Exercises actual registration and SDK signing with an ephemeral synthetic
// credential. Every HTTP transport is a rejecting sentinel; no ADC or files.
public sealed class CredentialRegistrationCompatibilityTests
{
    [Theory]
    [InlineData(1, 3600)]
    [InlineData(168, 604800)]
    [InlineData(720, 604800)]
    public async Task ActualRegistration_UsesOneInjectedClientCredentialForCachedSignerAndAdapter(int hours, int expectedSeconds)
    {
        using var rsa = RSA.Create(2048);
        var transport = new RejectingHttpClientFactory();
        const string identity = "file-registration-synthetic@example.invalid";
        var account = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(identity)
        {
            HttpClientFactory = transport,
        }.FromPrivateKey(rsa.ExportPkcs8PrivateKeyPem()));
        var services = new ServiceCollection();
        services.AddLogging();
        var clientCreations = 0;
        services.AddSingleton<StorageClient>(_ =>
        {
            clientCreations++;
            return new StorageClientBuilder
            {
                Credential = GoogleCredential.FromServiceAccountCredential(account),
                HttpClientFactory = transport,
            }.Build();
        });
        services.AddFileServiceRuntime(Configuration(enabled: true, legacyPath: "/must-not-be-opened/legacy-credential.json"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        Assert.IsType<GoogleCloudObjectStorage>(storage);
        Assert.Same(provider.GetRequiredService<UrlSigner>(), provider.GetRequiredService<UrlSigner>());
        Assert.Same(provider.GetRequiredService<StorageClient>(), scope.ServiceProvider.GetRequiredService<StorageClient>());

        var uri = await storage.CreateSignedReadUriAsync("synthetic-private", "orders/ค่าทำสี.stl", TimeSpan.FromHours(hours), default);
        var query = uri.Query.TrimStart('?').Split('&').Select(value => value.Split('=', 2))
            .ToDictionary(value => Uri.UnescapeDataString(value[0]), value => Uri.UnescapeDataString(value[1]), StringComparer.Ordinal);
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.Equal(expectedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), query["X-Goog-Expires"]);
        Assert.StartsWith(identity + "/", query["X-Goog-Credential"], StringComparison.Ordinal);
        Assert.Equal(512, query["X-Goog-Signature"].Length);
        Assert.Equal(1, clientCreations);
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/must-not-be-opened/legacy-credential.json")]
    public async Task DisabledRegistration_DoesNotAcquireCredentialsOrCreateSigner(string? legacyPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileServiceRuntime(Configuration(enabled: false, legacyPath));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(StorageClient));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(UrlSigner));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var storage = Assert.IsType<DisabledObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => storage.CreateSignedReadUriAsync(
            "synthetic-private", "orders/part.stl", TimeSpan.FromHours(1), default));
    }

    [Fact]
    public void ActualRegistration_UnsupportedSigningCredentialFailsWithoutCloudRequest()
    {
        var transport = new RejectingHttpClientFactory();
        using var client = new StorageClientBuilder
        {
            Credential = GoogleCredential.FromAccessToken("synthetic-non-signing-token"),
            HttpClientFactory = transport,
        }.Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(client);
        services.AddFileServiceRuntime(Configuration(enabled: true, legacyPath: null));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.Equal(0, transport.Requests);
    }

    private static IConfiguration Configuration(bool enabled, string? legacyPath) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileStorage:Enabled"] = enabled ? "true" : "false",
            ["FileStorage:WritesEnabled"] = "false",
            ["GoogleCloudStorage:CredentialsPath"] = legacyPath,
        }).Build();

    private sealed class RejectingHttpClientFactory : Google.Apis.Http.HttpClientFactory
    {
        private int requests;
        public int Requests => Volatile.Read(ref requests);

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new RejectingHandler(this);

        private sealed class RejectingHandler(RejectingHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.requests);
                return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Network is prohibited in the credential registration fixture."));
            }
        }
    }
}
