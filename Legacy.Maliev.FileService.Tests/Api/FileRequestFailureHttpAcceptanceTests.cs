using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Tests.OpenApi;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Api;

// Exercises actual Program/JWT/controller/shared middleware. Only the application
// call throws a controlled failure; this does not prove a real storage failure.
public sealed class FileRequestFailureHttpAcceptanceTests(FileOpenApiPostgresFixture database)
    : IClassFixture<FileOpenApiPostgresFixture>
{
    private const string Bucket = "private-bucket";
    private const string ObjectName = "private-customer@example.test/private-object.stl";
    private const string FailureDetail = "private-provider-detail";
    private const string Route = "uploads/SignedUrl";

    [Theory]
    [InlineData("unknown", HttpStatusCode.InternalServerError, LogLevel.Critical, "An internal server error occurred")]
    [InlineData("missing", HttpStatusCode.NotFound, LogLevel.Debug, "Resource not found")]
    [InlineData("invalid", HttpStatusCode.BadRequest, LogLevel.Critical, "The request is invalid.")]
    [InlineData("timeout", HttpStatusCode.RequestTimeout, LogLevel.Critical, "Request timeout")]
    public async Task ActualHttpFailure_EmitsOneRedactedIncidentMatchingGenericResponse(
        string kind, HttpStatusCode expectedStatus, LogLevel expectedLevel, string expectedError)
    {
        Exception failure = kind switch
        {
            "missing" => new KeyNotFoundException(FailureDetail),
            "invalid" => new ArgumentException(FailureDetail),
            "timeout" => new TimeoutException(FailureDetail),
            _ => new Exception(FailureDetail),
        };
        await using var factory = new IncidentFactory(database.ConnectionString, failure);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("read"));
        client.DefaultRequestHeaders.Add("X-Customer-Test", "private-header");
        using var response = await client.GetAsync(Query());

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["details", "error", "statusCode", "traceId"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedError, body.GetProperty("error").GetString());
        Assert.Equal((int)expectedStatus, body.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("details").ValueKind);
        var incidentId = body.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(incidentId));
        Assert.DoesNotContain("private-", body.ToString(), StringComparison.Ordinal);

        var incident = Assert.Single(factory.Logs.Entries,
            entry => entry.Category == typeof(ExceptionHandlingMiddleware).FullName);
        Assert.Equal(expectedLevel, incident.Level);
        Assert.Null(incident.Exception);
        Assert.Equal("UnhandledRequestFailure", incident.Values["EventName"]);
        Assert.Equal("Legacy.Maliev.FileService.Api", incident.Values["Service"]);
        Assert.Equal("GET", incident.Values["Method"]);
        Assert.Equal(Route, incident.Values["Path"]);
        Assert.Equal((int)expectedStatus, incident.Values["StatusCode"]);
        Assert.Equal(failure.GetType().Name, incident.Values["ExceptionType"]);
        Assert.Equal(incidentId, incident.Values["IncidentId"]);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(
            Assert.IsType<string>(incident.Values["OccurredAtUtc"]), "O", CultureInfo.InvariantCulture).Offset);
        AssertSafeOwnedLogs(factory.Logs);
        var completion = Assert.Single(factory.Logs.Entries,
            entry => entry.Category == typeof(RequestLoggingMiddleware).FullName && entry.Values.ContainsKey("StatusCode"));
        Assert.Equal((int)expectedStatus, completion.Values["StatusCode"]);
        factory.Service.Verify(service => service.GetSignedUrlAsync(Bucket, ObjectName, It.IsAny<CancellationToken>()), Times.Once);
        factory.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("anonymous", false, HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", false, HttpStatusCode.Unauthorized)]
    [InlineData("without-permission", false, HttpStatusCode.Forbidden)]
    [InlineData("read", true, HttpStatusCode.BadRequest)]
    public async Task ActualAdmissionFailure_DoesNotInvokeApplicationOrEmitIncident(
        string identity, bool missingQuery, HttpStatusCode expectedStatus)
    {
        await using var factory = new IncidentFactory(database.ConnectionString, new Exception(FailureDetail));
        using var client = factory.CreateClient();
        if (identity != "anonymous") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(identity));
        using var response = await client.GetAsync(missingQuery ? "/uploads/SignedUrl" : Query());

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.Unauthorized)
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
        Assert.DoesNotContain(factory.Logs.Entries,
            entry => entry.Category == typeof(ExceptionHandlingMiddleware).FullName);
        AssertSafeOwnedLogs(factory.Logs);
        factory.Service.VerifyNoOtherCalls();
    }

    private static string Query() => $"/uploads/SignedUrl?bucket={Bucket}&objectName={Uri.EscapeDataString(ObjectName)}&token=private-query";

    private static void AssertSafeOwnedLogs(CapturingProvider logs)
    {
        Assert.NotEmpty(logs.Entries);
        foreach (var entry in logs.Entries)
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(entry.Values), StringComparison.Ordinal);
            Assert.Equal(Route, entry.Values["Path"]);
        }
    }

    private sealed class IncidentFactory : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://file-incident-fixture.invalid";
        private const string Audience = "file-incident-fixture";
        private readonly string connectionString;
        private readonly RSA signingKey = RSA.Create(2048);
        public CapturingProvider Logs { get; } = new();
        public Mock<IFileService> Service { get; } = new(MockBehavior.Strict);

        public IncidentFactory(string connectionString, Exception failure)
        {
            this.connectionString = connectionString;
            Service.Setup(service => service.GetSignedUrlAsync(Bucket, ObjectName, It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);
        }

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
            };
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logging => logging
                .AddFilter("Maliev.Aspire.ServiceDefaults.Middleware", LogLevel.Trace).AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFileService>();
                services.AddSingleton(Service.Object);
            });
        }

        public string Token(string identity)
        {
            using var wrongKey = identity == "wrong-signature" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "private-customer") };
            if (identity is "read" or "wrong-signature") claims.Add(new("permissions", FilePermissions.Read));
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(wrongKey ?? signingKey), SecurityAlgorithms.RsaSha256)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) signingKey.Dispose();
        }
    }

    // Deliberately captures only the two owned middleware categories. No assertion
    // here certifies unrelated framework/provider logging or production telemetry.
    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => category == typeof(ExceptionHandlingMiddleware).FullName ||
            category == typeof(RequestLoggingMiddleware).FullName;
        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var values = ((IEnumerable<KeyValuePair<string, object?>>)state!).Where(pair => pair.Key != "{OriginalFormat}").ToDictionary();
            entries.Enqueue(new LogEntry(category, level, exception, formatter(state, exception), values));
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, Exception? Exception,
        string Message, Dictionary<string, object?> Values);
}
