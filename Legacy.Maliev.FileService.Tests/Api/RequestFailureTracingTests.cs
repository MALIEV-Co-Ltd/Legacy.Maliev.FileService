using System.Globalization;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.FileService.Tests.Api;

public sealed class RequestFailureTracingTests
{
    [Fact]
    public void FileService_UsesTheSharedRequestFailureBoundary()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
                   "Legacy.Maliev.FileService.Api", "Program.cs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var program = File.ReadAllText(Path.Combine(directory.FullName,
            "Legacy.Maliev.FileService.Api", "Program.cs"));
        Assert.Contains("builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);", program,
            StringComparison.Ordinal);
        Assert.Contains("app.UseStandardMiddleware();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("UseExceptionHandler(", program, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 500, LogLevel.Critical)]
    [InlineData(true, 404, LogLevel.Debug)]
    public async Task SharedBoundary_EmitsOneSafeIncidentAndPreservesGenericResponse(
        bool notFound, int expectedStatus, LogLevel expectedLevel)
    {
        var logger = new CapturingLogger();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "file-incident-123";
        context.Request.Method = "GET";
        context.Request.Path = "/Uploads/private-customer@example.test";
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Request.Headers.Authorization = "Bearer private-header";
        context.SetEndpoint(new RouteEndpointBuilder(
            _ => Task.CompletedTask, RoutePatternFactory.Parse("/Uploads/{id}"), 0).Build());
        Exception failure = notFound
            ? new KeyNotFoundException("private-provider-detail")
            : new Exception("private-provider-detail");
        var middleware = new ExceptionHandlingMiddleware(_ => throw failure, logger, new ProductionEnvironment());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("UnhandledRequestFailure", entry.Values["EventName"]);
        Assert.Equal("Legacy.Maliev.FileService.Api", entry.Values["Service"]);
        Assert.Equal("GET", entry.Values["Method"]);
        Assert.Equal("/Uploads/{id}", entry.Values["Path"]);
        Assert.Equal(expectedStatus, entry.Values["StatusCode"]);
        Assert.Equal(failure.GetType().Name, entry.Values["ExceptionType"]);
        Assert.Equal(context.TraceIdentifier, entry.Values["IncidentId"]);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(
            Assert.IsType<string>(entry.Values["OccurredAtUtc"]), "O", CultureInfo.InvariantCulture).Offset);
        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(expectedStatus, body.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(context.TraceIdentifier, body.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("private-", body.RootElement.ToString(), StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(level, exception, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).Where(pair => pair.Key != "{OriginalFormat}")
                .ToDictionary()));
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message,
        Dictionary<string, object?> Values);

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Legacy.Maliev.FileService.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
