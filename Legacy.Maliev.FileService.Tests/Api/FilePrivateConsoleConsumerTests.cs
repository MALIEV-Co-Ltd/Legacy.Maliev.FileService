using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Tests.OpenApi;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Legacy.Maliev.FileService.Tests.Api;

/// <summary>Checks File's actual production console selection and private failure output with storage activation closed.</summary>
/// <param name="database">The existing disposable PostgreSQL fixture used by File's actual Program tests.</param>
[Collection(FileIncidentHttpCollection.Name)]
public sealed class FilePrivateConsoleConsumerTests(FileOpenApiPostgresFixture database)
    : IClassFixture<FileOpenApiPostgresFixture>
{
    private const string Canary = "file-private-console-payload-sentinel";
    private const string ProbeName = "FilePrivateConsoleProbe";
    private const string Query = "/uploads/SignedUrl?bucket=maliev.com&objectName=private-file.stl&payload=" + Canary;

    /// <summary>Checks actual Program provider selection and rejects arbitrary text, state, scopes and successful severities.</summary>
    /// <param name="level">The severity delivered despite a deliberately permissive provider-specific probe rule.</param>
    /// <param name="severity">The expected private severity, or null when the formatter must remain quiet.</param>
    /// <returns>A task completing after production DI and selected-formatter output checks.</returns>
    [Theory]
    [InlineData(LogLevel.Trace, null)]
    [InlineData(LogLevel.Debug, null)]
    [InlineData(LogLevel.Information, null)]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public async Task ActualProgram_PrivateConsoleRejectsArbitraryTextAndSuccessLevels(LogLevel level, string? severity)
    {
        await using var host = new ConsoleHost(database.ConnectionString);
        using var client = host.Factory.CreateClient();
        var services = host.Factory.Services;
        var providers = services.GetServices<ILoggerProvider>().Where(provider => provider is not FormattedRecorder).ToArray();
        Assert.IsType<ConsoleLoggerProvider>(Assert.Single(providers));
        Assert.Equal(PrivateFailureConsoleFormatter.FormatterName,
            services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        Assert.True(services.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.NotNull(services.GetRequiredService<MeterProvider>());
        Assert.NotNull(services.GetRequiredService<TracerProvider>());
        AssertClosedStorage(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(ProbeName);
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        var formattedMessages = 0;
        using var activity = new Activity("FilePrivateConsoleRegression").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Payload"] = Canary,
            ["Authorization"] = Canary,
            ["service"] = Canary,
            ["traceId"] = Canary,
        });
        var state = new Dictionary<string, object?>
        {
            ["EventName"] = ProbeName,
            ["Operation"] = "PrivateConsoleRegression",
            ["StatusCode"] = 503,
            ["Payload"] = Canary,
            ["Email"] = Canary,
            ["Path"] = "/" + Canary,
            ["Authorization"] = Canary,
            ["service"] = Canary,
            ["deploymentVersion"] = Canary,
            ["traceId"] = Canary,
            ["spanId"] = Canary,
            ["exceptionType"] = Canary,
            ["innerExceptionType"] = Canary,
            ["sourceLocation"] = Canary,
        };
        logger.Log(level, new EventId(5251), state, CapturedFailure(), (_, _) =>
        {
            Interlocked.Increment(ref formattedMessages);
            return Canary;
        });

        Assert.Equal(0, formattedMessages);
        Assert.Equal(1, host.Records.ProbeInputs);
        var lines = host.Records.Lines(ProbeName);
        if (severity is null) Assert.Empty(lines);
        else
        {
            var line = Assert.Single(lines);
            Assert.DoesNotContain(Canary, line, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(line);
            Assert.Equal(severity, json.RootElement.GetProperty("severity").GetString());
            Assert.Equal(ProbeName, json.RootElement.GetProperty("EventName").GetString());
            Assert.Equal("PrivateConsoleRegression", json.RootElement.GetProperty("Operation").GetString());
            Assert.Equal(503, json.RootElement.GetProperty("StatusCode").GetInt32());
            Assert.Equal(typeof(InvalidOperationException).FullName, json.RootElement.GetProperty("exceptionType").GetString());
            Assert.Equal(typeof(IOException).FullName, json.RootElement.GetProperty("innerExceptionType").GetString());
            Assert.Equal(typeof(FilePrivateConsoleConsumerTests).FullName + "." + nameof(CapturedFailure),
                json.RootElement.GetProperty("sourceLocation").GetString());
            Assert.Equal(activity.TraceId.ToHexString(), json.RootElement.GetProperty("traceId").GetString());
            Assert.Equal(activity.SpanId.ToHexString(), json.RootElement.GetProperty("spanId").GetString());
            var entry = Assembly.GetEntryAssembly();
            Assert.Equal(entry?.GetName().Name, json.RootElement.GetProperty("service").GetString());
            var version = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Assert.Equal(version, json.RootElement.GetProperty("deploymentVersion").GetString());
            foreach (var field in new[] { "Payload", "Email", "Path", "Authorization" })
                Assert.False(json.RootElement.TryGetProperty(field, out _));
        }
        Assert.False(host.Records.Overflowed);
    }

    /// <summary>Preserves health, real JWT admission and disabled storage responses while formatting each existing failure once.</summary>
    /// <returns>A task completing after real HTTP responses and selected private failure output are checked.</returns>
    [Fact]
    public async Task ActualProgram_ExistingResponsesAndFailureEventsRemainPrivate()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var host = new ConsoleHost(database.ConnectionString))
        {
            using var client = host.Factory.CreateClient();
            AssertClosedStorage(host.Factory.Services);
            using var health = await client.GetAsync("/file/liveness", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(deadline.Token));
            Assert.True(Guid.TryParseExact(Assert.Single(health.Headers.GetValues("X-Maliev-Health-Instance")), "N", out _));
            using var anonymous = await client.GetAsync(Query, deadline.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Contains("Bearer", anonymous.Headers.WwwAuthenticate.Select(value => value.Scheme));
            client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
            using var disabled = await client.GetAsync(Query, deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
            using var problem = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync(deadline.Token));
            Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Legacy file service unavailable", problem.RootElement.GetProperty("title").GetString());
            AssertNoDiagnosticHeaders(anonymous);
            AssertNoDiagnosticHeaders(disabled);
            Assert.Single(host.Records.Lines("HandledOperationFailure"));
            Assert.Empty(host.Records.Lines("UnhandledRequestFailure"));
            Assert.Empty(host.Records.Lines("HealthProbeFailure"));
            AssertSafeRecords(host.Records);
        }

        await using (var host = new ConsoleHost(database.ConnectionString, throwApplicationFailure: true))
        {
            using var client = host.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
            using var failure = await client.GetAsync(Query, deadline.Token);
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            Assert.Equal("application/json", failure.Content.Headers.ContentType?.MediaType);
            var body = await failure.Content.ReadAsStringAsync(deadline.Token);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("An internal server error occurred", json.RootElement.GetProperty("error").GetString());
            Assert.Equal(500, json.RootElement.GetProperty("statusCode").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
            Assert.DoesNotContain(Canary, body, StringComparison.Ordinal);
            AssertNoDiagnosticHeaders(failure);
            Assert.Single(host.Records.Lines("UnhandledRequestFailure"));
            Assert.Empty(host.Records.Lines("HandledOperationFailure"));
            Assert.Empty(host.Records.Lines("HealthProbeFailure"));
            AssertSafeRecords(host.Records);
            host.Service.Verify(service => service.GetSignedUrlAsync("maliev.com", "private-file.stl", It.IsAny<CancellationToken>()), Times.Once);
            host.Service.VerifyNoOtherCalls();
            AssertClosedStorage(host.Factory.Services);
        }
    }

    private static InvalidOperationException CapturedFailure()
    {
        try { throw new InvalidOperationException(Canary, new IOException(Canary)); }
        catch (InvalidOperationException failure) { return failure; }
    }

    private static void AssertClosedStorage(IServiceProvider services)
    {
        var storage = services.GetRequiredService<IOptions<FileStorageOptions>>().Value;
        Assert.False(storage.Enabled);
        Assert.False(storage.WritesEnabled);
        var quotation = services.GetRequiredService<IOptions<InstantQuoteFileOptions>>().Value;
        Assert.False(quotation.Enabled);
        Assert.False(quotation.WritesEnabled);
        Assert.False(quotation.CleanupEnabled);
    }

    private static void AssertNoDiagnosticHeaders(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
    }

    private static void AssertSafeRecords(FormattedRecorder records)
    {
        Assert.NotEmpty(records.AllLines);
        foreach (var line in records.AllLines) Assert.DoesNotContain(Canary, line, StringComparison.Ordinal);
        Assert.False(records.Overflowed);
    }

    private sealed class ConsoleHost : IAsyncDisposable
    {
        private const string Issuer = "https://file-console-fixture.invalid";
        private const string Audience = "file-console-fixture";
        private readonly RSA signingKey = RSA.Create(2048);
        internal ConsoleFactory Factory { get; }
        internal Mock<IFileService> Service { get; } = new(MockBehavior.Strict);
        internal FormattedRecorder Records => Factory.Services.GetServices<ILoggerProvider>().OfType<FormattedRecorder>().Single();

        internal ConsoleHost(string connectionString, bool throwApplicationFailure = false)
        {
            try
            {
                if (throwApplicationFailure)
                    Service.Setup(service => service.GetSignedUrlAsync("maliev.com", "private-file.stl", It.IsAny<CancellationToken>()))
                        .Throws(new IOException(Canary));
                Factory = new ConsoleFactory(connectionString, signingKey, throwApplicationFailure ? Service.Object : null);
            }
            catch { signingKey.Dispose(); throw; }
        }

        internal string Token()
        {
            var now = DateTime.UtcNow;
            Claim[] claims = [new(JwtRegisteredClaimNames.Sub, "file-console-fixture"), new("permissions", FilePermissions.Read)];
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256)));
        }

        public async ValueTask DisposeAsync()
        {
            try { await Factory.DisposeAsync(); }
            finally { signingKey.Dispose(); }
        }

        internal sealed class ConsoleFactory(string connectionString, RSA signingKey, IFileService? service) : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Production");
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FileDbContext"] = connectionString,
                    ["Cache:RedisEnabled"] = "false",
                    ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())),
                    ["Jwt:Issuer"] = Issuer,
                    ["Jwt:Audience"] = Audience,
                    ["FileStorage:Enabled"] = "false",
                    ["FileStorage:WritesEnabled"] = "false",
                    ["InstantQuoteFiles:Enabled"] = "false",
                    ["InstantQuoteFiles:WritesEnabled"] = "false",
                    ["InstantQuoteFiles:CleanupEnabled"] = "false",
                    ["Observability:TracingEnabled"] = "true",
                    ["Observability:RuntimeMetricsEnabled"] = "false",
                    ["Logging:Console:LogLevel:" + ProbeName] = "Trace",
                };
                foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
                builder.ConfigureTestServices(services =>
                {
                    if (service is not null)
                    {
                        services.RemoveAll<IFileService>();
                        services.AddSingleton(service);
                    }
                    services.AddLogging(logging => logging.AddFilter<FormattedRecorder>(ProbeName, LogLevel.Trace));
                    services.AddSingleton<ILoggerProvider>(provider => new FormattedRecorder(
                        provider.GetServices<ConsoleFormatter>().Single(formatter => formatter.Name ==
                            provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName)));
                    services.Configure<HostOptions>(options =>
                    {
                        options.StartupTimeout = TimeSpan.FromSeconds(20);
                        options.ShutdownTimeout = TimeSpan.FromSeconds(10);
                    });
                });
            }
        }
    }

    private sealed class FormattedRecorder(ConsoleFormatter formatter) : ILoggerProvider
    {
        private readonly ConsoleFormatter selected = formatter;
        private readonly ConcurrentQueue<(string Event, string Line)> records = new();
        private int count;
        private int probeInputs;
        internal int ProbeInputs => Volatile.Read(ref probeInputs);
        internal bool Overflowed => Volatile.Read(ref count) > 128;
        internal IReadOnlyList<string> AllLines => records.Select(record => record.Line).ToArray();
        internal IReadOnlyList<string> Lines(string name) => records.Where(record => record.Event == name).Select(record => record.Line).ToArray();
        public ILogger CreateLogger(string category) => new Recorder(this, category);
        public void Dispose() { }

        private sealed class Recorder(FormattedRecorder owner, string category) : ILogger
        {
            private readonly LoggerExternalScopeProvider scopes = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> message)
            {
                var name = (state as IEnumerable<KeyValuePair<string, object?>>)?
                    .Take(64).FirstOrDefault(pair => pair.Key == "EventName").Value as string;
                if (name is not ("FilePrivateConsoleProbe" or "HandledOperationFailure" or "UnhandledRequestFailure" or "HealthProbeFailure")) return;
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, id, state, exception, message);
                if (name == ProbeName) Interlocked.Increment(ref owner.probeInputs);
                owner.selected.Write(in entry, scopes, writer);
                var line = writer.ToString().Trim();
                if (line.Length == 0) return;
                if (Interlocked.Increment(ref owner.count) <= 128) owner.records.Enqueue((name, line));
            }
        }
    }
}
