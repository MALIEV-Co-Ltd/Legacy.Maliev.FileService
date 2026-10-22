using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.FileService.Tests.OpenApi;

public sealed class LegacyUploadHealthHttpTests
{
    [Fact]
    public async Task LegacyAndCurrentLiveness_StayAnonymousAndNeverEvaluateDependencies()
    {
        await using var factory = new HealthFactory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/uploads/liveness", "/file/liveness" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(deadline.Token));
            if (path.StartsWith("/uploads", StringComparison.Ordinal))
            {
                Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
                Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
            }
            else
            {
                var instance = Assert.Single(response.Headers.GetValues("X-Maliev-Health-Instance"));
                Assert.True(Guid.TryParseExact(instance, "N", out _));
            }
        }
        Assert.Equal(0, factory.Probe.Calls);
    }

    [Fact]
    public async Task LegacyReadiness_PreservesUnhealthyStatusAndHidesPrivateDetails()
    {
        await using var factory = new HealthFactory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/uploads/readiness", "/file/readiness" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await client.GetAsync(path, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("PRIVATE-PROBE-DETAIL", body, StringComparison.Ordinal);
            if (path.StartsWith("/uploads", StringComparison.Ordinal))
            {
                Assert.Equal("Unhealthy", body);
                Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
        }
        Assert.Equal(4, factory.Probe.Calls);
        var first = Assert.Single(factory.Observation.Snapshot());
        Assert.Equal("HealthProbeFailure", first.EventName);
        Assert.Equal("Readiness", first.Operation);
        Assert.Equal(503, first.StatusCode);
        Assert.False(first.ExceptionPresent);
        Assert.Equal(new[] { "EventName", "Operation", "StatusCode", "{OriginalFormat}" }, first.Keys);
        using var resetDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        factory.Probe.Status = HealthStatus.Healthy;
        using (var success = await client.GetAsync("/uploads/readiness", resetDeadline.Token))
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        factory.Probe.Status = HealthStatus.Unhealthy;
        using (var repeated = await client.GetAsync("/file/readiness", resetDeadline.Token))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, repeated.StatusCode);
        Assert.Equal(8, factory.Probe.Calls);
        var afterReset = factory.Observation.Snapshot();
        Assert.Equal(2, afterReset.Length);
        Assert.All(afterReset, entry =>
        {
            Assert.Equal("HealthProbeFailure", entry.EventName);
            Assert.Equal("Readiness", entry.Operation);
            Assert.Equal(503, entry.StatusCode);
            Assert.False(entry.ExceptionPresent);
        });
        Assert.False(factory.Observation.Overflowed);
    }

    [Fact]
    public async Task LegacyReadiness_EvaluatesUntaggedChecksUnderCurrentStrongerPolicy()
    {
        await using var factory = new HealthFactory(HealthStatus.Degraded);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("/uploads/readiness", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal(2, factory.Probe.Calls);
    }

    [Fact]
    public async Task TrustedHttpEdge_ExactLegacyProbesRemainReachableForEveryUnprovenScheme()
    {
        await using var factory = new HealthFactory(HealthStatus.Healthy, configuredProxy: true);
        foreach (var scheme in new string?[] { "http", null, "not a scheme", "https,http" })
            foreach (var path in new[] { "/uploads/liveness", "/uploads/readiness", "/UpLoAdS/LiVeNeSs", "/UpLoAdS/ReAdInEsS" })
            {
                var before = factory.Probe.Calls;
                var context = await SendThroughProxyAsync(factory, path, scheme);
                Assert.Equal(200, context.Response.StatusCode);
                Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
                Assert.Equal(0, context.Response.Headers.Location.Count);
                Assert.Equal(0, context.Response.Headers["Strict-Transport-Security"].Count);
                Assert.Equal(before + (path.EndsWith("readiness", StringComparison.OrdinalIgnoreCase) ? 2 : 0), factory.Probe.Calls);
            }
    }

    [Fact]
    public async Task TrustedHttpEdge_NearHealthAndBusinessPathsRemainHttpsGatedBeforeAuthentication()
    {
        await using var factory = new HealthFactory(HealthStatus.Healthy, configuredProxy: true);
        foreach (var scheme in new string?[] { "http", null, "not a scheme", "https,http" })
            foreach (var path in new[] { "/uploads/liveness/", "/uploads/readiness/", "/uploads/liveness-more", "/uploads/readiness/child", "/uploads/SignedUrl", "/Uploads" })
            {
                var context = await SendThroughProxyAsync(factory, path, scheme);
                Assert.Equal(426, context.Response.StatusCode);
                Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
                Assert.Equal(0, context.Response.Headers.Location.Count);
            }
        Assert.Equal(0, factory.Probe.Calls);
    }

    private static async Task<HttpContext> SendThroughProxyAsync(HealthFactory factory, string path, string? forwardedScheme)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.40");
            context.Request.Scheme = "http";
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.Request.Host = new HostString("file-health-fixture.invalid");
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.70";
            if (forwardedScheme is not null) context.Request.Headers["X-Forwarded-Proto"] = forwardedScheme;
        }, deadline.Token);
    }

    private sealed class HealthFactory(HealthStatus status, bool configuredProxy = false) : WebApplicationFactory<Program>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public ControlledProbe Probe { get; } = new(status);
        public ObservationCapture Observation { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.AddProvider(Observation));
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:FileDbContext"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused",
                ["Cache:RedisEnabled"] = "false",
                ["ForwardedHeaders:KnownProxies:0"] = configuredProxy ? "192.0.2.40" : "",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://file-health-fixture.invalid",
                ["Jwt:Audience"] = "file-health-fixture",
                ["FileStorage:Enabled"] = "false",
                ["FileStorage:WritesEnabled"] = "false",
                ["InstantQuoteFiles:Enabled"] = "false",
                ["InstantQuoteFiles:WritesEnabled"] = "false",
                ["InstantQuoteFiles:CleanupEnabled"] = "false",
            };
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services => services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                // Route admission uses the actual Program; only dependency health outcomes are controlled.
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration("tagged", _ => Probe, null, ["ready"]));
                options.Registrations.Add(new HealthCheckRegistration("untagged", _ => Probe, null, []));
            }));
        }

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) signingKey.Dispose(); }
        }
    }

    // Bounded native observation metadata only; no formatted text, health details or request values.
    private sealed class ObservationCapture : ILoggerProvider
    {
        private readonly object gate = new();
        private readonly List<Entry> entries = [];
        public bool Overflowed { get; private set; }
        public sealed record Entry(string? EventName, string? Operation, int? StatusCode, bool ExceptionPresent, string[] Keys);

        public Entry[] Snapshot()
        {
            lock (gate) return entries.ToArray();
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this,
            categoryName == "Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateRequestObservation");
        public void Dispose() { }

        private sealed class CaptureLogger(ObservationCapture owner, bool observation) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => observation && logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                var properties = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToArray() : [];
                var entry = new Entry(
                    properties.SingleOrDefault(pair => pair.Key == "EventName").Value as string,
                    properties.SingleOrDefault(pair => pair.Key == "Operation").Value as string,
                    properties.SingleOrDefault(pair => pair.Key == "StatusCode").Value is int code ? code : null,
                    exception is not null,
                    properties.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
                lock (owner.gate)
                {
                    if (owner.entries.Count == 16) owner.Overflowed = true;
                    else owner.entries.Add(entry);
                }
            }
        }
    }
    private sealed class ControlledProbe(HealthStatus status) : IHealthCheck
    {
        private int calls;
        private int currentStatus = (int)status;
        public HealthStatus Status
        {
            get => (HealthStatus)Volatile.Read(ref currentStatus);
            set => Volatile.Write(ref currentStatus, (int)value);
        }
        public int Calls => Volatile.Read(ref calls);

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HealthCheckResult(Status, "PRIVATE-PROBE-DETAIL"));
        }
    }
}
