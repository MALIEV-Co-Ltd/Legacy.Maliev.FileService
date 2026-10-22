using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Api.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.FileService.Tests.OpenApi;

// Actual normal Program, forwarding/HSTS/CORS/auth middleware and disposable PG.
// TestServer controls transport metadata; these are not live ingress/TLS proofs.
public sealed class FileHostTransportHttpTests(FileOpenApiPostgresFixture database)
    : IClassFixture<FileOpenApiPostgresFixture>
{
    private const string Proxy = "192.0.2.40";
    private const string Caller = "198.51.100.90";
    private const string ApiPath = "/uploads/SignedUrl";

    [Theory]
    [InlineData("/file/liveness", false, 200)]
    [InlineData("/file/readiness", false, 200)]
    [InlineData(ApiPath, true, 503)]
    public async Task InternalHttp_PreservesProbeAndRealBearerAdmission(string path, bool bearer, int status)
    {
        await using var factory = Factory();
        var context = await SendAsync(factory, path, Caller, bearer: bearer);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers["Strict-Transport-Security"].Count);
        Assert.Equal(0, context.Response.Headers.Location.Count);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task DirectHttps_UsesProductionHstsWithoutRedirect(string environment, bool hsts)
    {
        await using var factory = Factory(environment: environment);
        var context = await SendAsync(factory, ApiPath, Caller, scheme: "https", bearer: true);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(hsts ? "max-age=2592000" : "", context.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.Equal(0, context.Response.Headers.Location.Count);
    }

    [Theory]
    [InlineData(Proxy)]
    [InlineData("::ffff:192.0.2.40")]
    public async Task TrustedEdgeHttps_UsesActualForwarderAndRealBearerAdmission(string remote)
    {
        await using var factory = Factory();
        var context = await SendAsync(factory, ApiPath, remote, forwardedScheme: "https", bearer: true);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse(Caller), context.Connection.RemoteIpAddress);
        Assert.Equal("max-age=2592000", context.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.Equal(0, context.Request.Headers["X-File-Original-Scheme"].Count);
    }

    [Theory]
    [InlineData("http")]
    [InlineData(null)]
    [InlineData("not a scheme")]
    [InlineData("https,http")]
    public async Task TrustedEdge_InsecureOrUnprovenSchemeRejectsBeforeAuthentication(string? forwardedScheme)
    {
        await using var factory = Factory();
        var context = await SendAsync(factory, ApiPath, Proxy, forwardedScheme: forwardedScheme);
        Assert.Equal(426, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        Assert.Equal("application/problem+json", context.Response.ContentType);
    }

    [Theory]
    [InlineData(Caller, true)]
    [InlineData(Proxy, false)]
    [InlineData(null, true)]
    public async Task UntrustedOrUnconfiguredPeer_CannotSpoofForwardedHttpsOrOriginalMarker(string? remote, bool configuredProxy)
    {
        await using var factory = Factory(configuredProxy: configuredProxy);
        var context = await SendAsync(factory, ApiPath, remote, forwardedScheme: "https", bearer: true, spoofOriginal: true);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal(remote is null ? null : IPAddress.Parse(remote), context.Connection.RemoteIpAddress);
        Assert.Equal(0, context.Response.Headers["Strict-Transport-Security"].Count);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        Assert.Equal(0, context.Request.Headers["X-File-Original-Scheme"].Count);
        Assert.Equal(0, context.Request.Headers["X-Original-Proto"].Count);
        if (!configuredProxy)
        {
            var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            Assert.Empty(options.KnownProxies);
            Assert.Empty(options.KnownIPNetworks);
            Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
        }
    }

    [Theory]
    [InlineData("/file/liveness")]
    [InlineData("/file/readiness")]
    [InlineData("/file/aspire-liveness")]
    public async Task RegisteredProbes_RemainReachableThroughTrustedHttp(string path)
    {
        await using var factory = Factory();
        var context = await SendAsync(factory, path, Proxy, forwardedScheme: "http");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        Assert.Equal(0, context.Response.Headers["Strict-Transport-Security"].Count);
    }

    [Theory]
    [InlineData("https://allowed.example.invalid", true)]
    [InlineData("https://unlisted.example.invalid", false)]
    public async Task ExplicitCorsPreflight_CompletesBeforeAuthenticationAtTrustedHttpsEdge(string origin, bool allowed)
    {
        await using var factory = Factory();
        var context = await factory.Server.SendAsync(context =>
        {
            ConfigureRequest(context, "/Uploads", Proxy, "http", "https", false, factory);
            context.Request.Method = "OPTIONS";
            context.Request.Headers.Origin = origin;
            context.Request.Headers["Access-Control-Request-Method"] = "POST";
            context.Request.Headers["Access-Control-Request-Headers"] = "authorization,content-type";
        });
        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(allowed ? origin : "", context.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
        Assert.Equal("max-age=2592000", context.Response.Headers["Strict-Transport-Security"].ToString());
    }

    [Fact]
    public async Task UnsupportedTransportPolicy_FailsNormalStartup()
    {
        await using var factory = Factory(policy: "trust-everything");
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task RegisteredGetHealth_IdentifiesOnlyItsHostWithoutSuccessfulProbeIncidents()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var factory = Factory(observe: true);
        using var client = factory.CreateClient();
        string? instance = null;
        foreach (var path in new[] { "/file/liveness", "/file/readiness", "/file/liveness" })
        {
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var current = Assert.Single(response.Headers.GetValues("X-Maliev-Health-Instance"));
            Assert.True(Guid.TryParseExact(current, "N", out _));
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            instance ??= current;
            Assert.Equal(instance, current);
        }
        await using var other = Factory(observe: true);
        using var otherClient = other.CreateClient();
        using var independent = await otherClient.GetAsync("/file/liveness", deadline.Token);
        Assert.NotEqual(instance, Assert.Single(independent.Headers.GetValues("X-Maliev-Health-Instance")));
        foreach (var path in new[] { "/file/aspire-liveness", "/file/openapi/v1.json", "/not-a-health-endpoint", ApiPath })
        {
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
            Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        }
        using var post = await client.PostAsync("/file/liveness", null, deadline.Token);
        Assert.False(post.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Empty(factory.Observation.Snapshot());
        Assert.Empty(other.Observation.Snapshot());
        Assert.False(factory.Observation.Overflowed);
        Assert.False(other.Observation.Overflowed);
    }

    [Fact]
    public async Task SyntheticObservation_RequiresNativeLoopbackAndOneNonceAndPreservesThrottling()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var factory = Factory(observe: true);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/file/liveness", deadline.Token);
        var instance = Assert.Single(health.Headers.GetValues("X-Maliev-Health-Instance"));
        const string path = "/internal/diagnostics/observability";
        const string nonce = "0123456789abcdef0123456789abcdef";
        foreach (var denied in new[]
        {
            (Remote: (string?)Caller, Nonce: (string?)nonce, Method: "GET", Duplicate: false),
            (Remote: (string?)null, Nonce: (string?)nonce, Method: "GET", Duplicate: false),
            (Remote: (string?)"127.0.0.1", Nonce: (string?)null, Method: "GET", Duplicate: false),
            (Remote: (string?)"127.0.0.1", Nonce: (string?)"invalid", Method: "GET", Duplicate: false),
            (Remote: (string?)"127.0.0.1", Nonce: (string?)nonce, Method: "POST", Duplicate: false),
            (Remote: (string?)"127.0.0.1", Nonce: (string?)nonce, Method: "GET", Duplicate: true),
        })
        {
            var response = await factory.Server.SendAsync(context =>
            {
                ConfigureRequest(context, path, denied.Remote, "http", null, false, factory);
                context.Request.Method = denied.Method;
                context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
                if (denied.Nonce is not null)
                    context.Request.Headers["X-Maliev-Diagnostic-Id"] = denied.Duplicate
                        ? new Microsoft.Extensions.Primitives.StringValues([denied.Nonce, denied.Nonce])
                        : denied.Nonce;
            }, deadline.Token);
            Assert.Equal(404, response.Response.StatusCode);
            Assert.False(response.Response.Headers.ContainsKey("X-Maliev-Health-Instance"));
            Assert.False(response.Response.Headers.ContainsKey("X-Maliev-Diagnostic-Id"));
        }
        Assert.Empty(factory.Observation.Snapshot());
        var admitted = await factory.Server.SendAsync(context =>
        {
            ConfigureRequest(context, path, "127.0.0.1", "http", null, false, factory);
            context.Request.Headers["X-Maliev-Diagnostic-Id"] = nonce;
        }, deadline.Token);
        Assert.Equal(500, admitted.Response.StatusCode);
        Assert.Equal(instance, admitted.Response.Headers["X-Maliev-Health-Instance"].ToString());
        Assert.Equal(nonce, admitted.Response.Headers["X-Maliev-Diagnostic-Id"].ToString());
        Assert.Equal("no-store", admitted.Response.Headers.CacheControl.ToString());
        Assert.Equal("noindex", admitted.Response.Headers["X-Robots-Tag"].ToString());
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical },
            factory.Observation.Snapshot().Select(entry => entry.Level));
        var throttled = await factory.Server.SendAsync(context =>
        {
            ConfigureRequest(context, path, "127.0.0.1", "http", null, false, factory);
            context.Request.Headers["X-Maliev-Diagnostic-Id"] = nonce;
        }, deadline.Token);
        Assert.Equal(429, throttled.Response.StatusCode);
        Assert.False(throttled.Response.Headers.ContainsKey("X-Maliev-Health-Instance"));
        Assert.False(throttled.Response.Headers.ContainsKey("X-Maliev-Diagnostic-Id"));
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical },
            factory.Observation.Snapshot().Select(entry => entry.Level));
        Assert.False(factory.Observation.Overflowed);
    }

    [Fact]
    public async Task DisabledSignedRead_ProducesOneSanitizedIncidentWithoutSyntheticOrHealthIdentity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var factory = Factory(observe: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var response = await client.GetAsync(
            "/uploads/SignedUrl?bucket=synthetic-private-marker&objectName=synthetic-confidential-marker.stl", deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Legacy file service unavailable", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("File storage is temporarily unavailable.", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("synthetic-private-marker", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-confidential-marker", body, StringComparison.Ordinal);
        var incident = Assert.Single(factory.Observation.Snapshot());
        Assert.Equal(LogLevel.Error, incident.Level);
        Assert.Equal("HandledOperationFailure", incident.EventName);
        Assert.Equal("HttpResponse", incident.Operation);
        Assert.Equal(503, incident.StatusCode);
        Assert.Equal(new[] { "EventName", "Operation", "StatusCode", "{OriginalFormat}" }, incident.Keys);
        Assert.False(factory.Observation.Overflowed);
    }

    private FileHostFactory Factory(bool configuredProxy = true, string environment = "Production",
        string policy = "InternalHttpWithTrustedEdgeHttps", bool observe = false) =>
        new(database.ConnectionString, configuredProxy, environment, policy, observe: observe);

    private static Task<HttpContext> SendAsync(FileHostFactory factory, string path, string? remote,
        string scheme = "http", string? forwardedScheme = null, bool bearer = false, bool spoofOriginal = false) =>
        factory.Server.SendAsync(context =>
        {
            ConfigureRequest(context, path, remote, scheme, forwardedScheme, bearer, factory);
            if (spoofOriginal)
            {
                context.Request.Headers["X-File-Original-Scheme"] = "http";
                context.Request.Headers["X-Original-Proto"] = "https";
            }
        });

    private static void ConfigureRequest(HttpContext context, string path, string? remote, string scheme,
        string? forwardedScheme, bool bearer, FileHostFactory factory)
    {
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        context.Request.Method = "GET";
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("file.example.invalid");
        context.Request.Path = path;
        if (path == ApiPath) context.Request.QueryString = new QueryString("?bucket=private&objectName=part.stl");
        if (forwardedScheme is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = forwardedScheme;
            context.Request.Headers["X-Forwarded-For"] = Caller;
        }
        if (bearer) context.Request.Headers.Authorization = "Bearer " + factory.Token();
    }

    [Fact]
    public async Task HostedHandshakeRequiresRealBearerReturnsOnlyPublicKeyAndHonorsExpiry()
    {
        var clock = new HostedClock();
        await using var factory = new FileHostFactory(database.ConnectionString, false,
            HostedFinancialCompletionProfile.EnvironmentName, "InternalHttpWithTrustedEdgeHttps", clock);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://fixture.invalid") });
        using var anonymous = await client.GetAsync("/file/acceptance/signing-key");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var authorized = await client.GetAsync("/file/acceptance/signing-key");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var body = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "Algorithm", "PublicKey" }, body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("GOOG4-RSA-SHA256", body.RootElement.GetProperty("Algorithm").GetString());
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(body.RootElement.GetProperty("PublicKey").GetString()!), out _);
        Assert.Equal(2048, key.KeySize);
        clock.Current = clock.Current.AddMinutes(11);
        using var expired = await client.GetAsync("/file/acceptance/signing-key");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, expired.StatusCode);
    }

    // Capture only bounded event metadata, never formatter output, exception details or request values.
    private sealed class ObservationCapture : ILoggerProvider
    {
        private readonly object gate = new();
        private readonly List<Entry> entries = [];
        public bool Overflowed { get; private set; }
        public sealed record Entry(LogLevel Level, string? EventName, string? Operation, int? StatusCode, string[] Keys);
        public Entry[] Snapshot()
        {
            lock (gate) return entries.ToArray();
        }
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this,
            categoryName == "Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateRequestObservation",
            categoryName == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        public void Dispose() { }

        private sealed class CaptureLogger(ObservationCapture owner, bool observation, bool framework) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => (observation || framework) && logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var properties = observation && state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToArray() : [];
                var entry = new Entry(logLevel,
                    properties.SingleOrDefault(pair => pair.Key == "EventName").Value as string,
                    properties.SingleOrDefault(pair => pair.Key == "Operation").Value as string,
                    properties.SingleOrDefault(pair => pair.Key == "StatusCode").Value is int status ? status : null,
                    properties.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
                lock (owner.gate)
                {
                    if (owner.entries.Count == 16) owner.Overflowed = true;
                    else owner.entries.Add(entry);
                }
            }
        }
    }

    private sealed class HostedClock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class FileHostFactory(string connectionString, bool configuredProxy, string environment, string policy, HostedClock? hostedClock = null, bool observe = false)
        : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://file-host-policy-fixture.invalid";
        private const string Audience = "file-host-policy";
        private readonly RSA signingKey = RSA.Create(2048);
        public ObservationCapture Observation { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            if (observe) builder.ConfigureLogging(logging => logging.AddProvider(Observation));
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:FileDbContext"] = connectionString,
                ["Cache:RedisEnabled"] = "false",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["CORS:AllowedOrigins:0"] = "https://allowed.example.invalid",
                ["ForwardedHeaders:KnownProxies:0"] = configuredProxy ? Proxy : "",
                ["FileHost:TransportPolicy"] = policy,
                ["FileStorage:Enabled"] = "false",
                ["FileStorage:WritesEnabled"] = "false",
                ["InstantQuoteFiles:Enabled"] = "false",
                ["InstantQuoteFiles:WritesEnabled"] = "false",
                ["InstantQuoteFiles:CleanupEnabled"] = "false"
            };
            if (hostedClock is not null)
            {
                var prefix = "HostedFinancialCompletionAcceptance:Admission:";
                settings["HostedFinancialCompletionAcceptance:Enabled"] = "true";
                settings["FileStorage:Enabled"] = "true";
                settings["FileStorage:WritesEnabled"] = "true";
                settings["FileStorage:AllowedBuckets:0"] = "synthetic-private";
                settings[prefix + "SchemaVersion"] = "1";
                settings[prefix + "RunId"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
                settings[prefix + "RunAttempt"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
                settings[prefix + "FileSourceSha"] = HostedFinancialCompletionProfile.BuildSourceSha();
                settings[prefix + "IssuedUtc"] = hostedClock.Current.ToString("O");
                settings[prefix + "ExpiresUtc"] = hostedClock.Current.AddMinutes(10).ToString("O");
                settings[prefix + "StorageOrigin"] = "http://127.0.0.1:5010/";
                settings[prefix + "ResourceLeaseId"] = "c821-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
                settings[prefix + "StorageEndpointIdentity:Kind"] = "process";
                settings[prefix + "StorageEndpointIdentity:Pid"] = "123";
                settings[prefix + "StorageEndpointIdentity:StartedUtc"] = hostedClock.Current.AddMinutes(-1).ToString("O");
                settings[prefix + "StorageEndpointIdentity:ExecutableAbsolutePath"] = "/usr/bin/dotnet";
                settings[prefix + "StorageEndpointIdentity:ExecutableSha256"] = new string('a', 64);
                settings[prefix + "StorageEndpointIdentity:HostIp"] = "127.0.0.1";
                settings[prefix + "StorageEndpointIdentity:HostPort"] = "5010";
                settings[prefix + "ScannerHost"] = "127.0.0.1";
                settings[prefix + "ScannerPort"] = "3310";
                settings[prefix + "ScannerContainerId"] = new string('b', 64);
                settings[prefix + "ScannerImageDigest"] = "sha256:" + new string('c', 64);
                settings[prefix + "ScannerDatabaseIdentity:EngineVersion"] = "controlled-protocol-fixture";
                settings[prefix + "ScannerDatabaseIdentity:LoadedDatabaseVersion"] = "controlled-not-engine-evidence";
                settings[prefix + "ScannerDatabaseIdentity:ObservedUtc"] = hostedClock.Current.ToString("O");
                settings[prefix + "ScannerDatabaseIdentity:ReadinessReceiptSha256"] = new string('d', 64);
                settings[prefix + "ScannerDatabaseIdentity:DatabaseFilesSha256:daily.cvd"] = new string('e', 64);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(hostedClock);
                });
            }
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        }

        public string Token()
        {
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience,
                [new Claim(JwtRegisteredClaimNames.Sub, "file-host-policy-fixture"), new Claim("permissions", FilePermissions.Read)],
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256)));
        }

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) signingKey.Dispose(); }
        }
    }
}
