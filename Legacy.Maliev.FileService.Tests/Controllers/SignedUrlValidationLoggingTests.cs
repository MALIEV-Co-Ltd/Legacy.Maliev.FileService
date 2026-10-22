using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Controllers;

public sealed class SignedUrlValidationLoggingTests
{
    [Fact]
    public async Task MissingCoordinates_EmitOriginalPrivateWarningWithoutCallingService()
    {
        foreach (var (bucket, objectName) in new (string?, string?)[]
        {
            (null, null), (null, "PRIVATE-FILE-OBJECT"), ("", "PRIVATE-FILE-OBJECT"),
            ("PRIVATE-FILE-BUCKET", null), ("PRIVATE-FILE-BUCKET", ""), ("", ""),
        }) await AssertMissingAsync(bucket, objectName);
    }

    private static async Task AssertMissingAsync(string? bucket, string? objectName)
    {
        var service = new Mock<IFileService>(MockBehavior.Strict);
        var logger = new RecordingLogger();
        using var provider = Provider(logger);
        var controller = Controller(provider, service.Object);

        var result = await controller.GetSignedUrlAsync(bucket, objectName, default);

        Assert.IsType<BadRequestResult>(result.Result);
        service.VerifyNoOtherCalls();
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("UploadValidationFailure", entry.Properties["EventName"]);
        Assert.Equal("GetSignedUrl", entry.Properties["Operation"]);
        Assert.Equal("BucketOrObjectNameMissing", entry.Properties["ValidationError"]);
        Assert.Equal("{EventName} Operation={Operation} ValidationError={ValidationError}", entry.Properties["{OriginalFormat}"]);
        Assert.Equal(new[] { "EventName", "Operation", "ValidationError", "{OriginalFormat}" },
            entry.Properties.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("PRIVATE-FILE", entry.Message, StringComparison.Ordinal);
        Assert.All(entry.Properties.Values, value => Assert.DoesNotContain("PRIVATE-FILE", value?.ToString() ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousCoordinates_PreserveBadRequestWithoutClaimingMissingInput()
    {
        foreach (var query in new[] { "?bucket=first&bucket=second", "?objectName=first&objectName=second", "?Bucket=first&bucket=second" })
            await AssertAmbiguousAsync(query);
    }

    private static async Task AssertAmbiguousAsync(string query)
    {
        var service = new Mock<IFileService>(MockBehavior.Strict);
        var logger = new RecordingLogger();
        using var provider = Provider(logger);
        var controller = Controller(provider, service.Object);
        controller.Request.QueryString = new QueryString(query);

        var result = await controller.GetSignedUrlAsync("PRIVATE-FILE-BUCKET", "PRIVATE-FILE-OBJECT", default);

        Assert.IsType<BadRequestResult>(result.Result);
        service.VerifyNoOtherCalls();
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task PresentCoordinates_PreserveExistingServiceResultsWithoutMissingWarning()
    {
        foreach (var outcome in new[] { "found", "missing", "invalid" }) await AssertPresentAsync(outcome);
    }

    private static async Task AssertPresentAsync(string outcome)
    {
        var uri = new Uri("https://example.invalid/private-object");
        var service = new Mock<IFileService>(MockBehavior.Strict);
        var call = service.Setup(value => value.GetSignedUrlAsync("bucket", "object", It.IsAny<CancellationToken>()));
        if (outcome == "invalid") call.ThrowsAsync(new FileUploadValidationException("Invalid object coordinate."));
        else call.ReturnsAsync(outcome == "found" ? uri : null);
        var logger = new RecordingLogger();
        using var provider = Provider(logger);
        var controller = Controller(provider, service.Object);

        var result = await controller.GetSignedUrlAsync("bucket", "object", default);

        if (outcome == "found") Assert.Equal(uri, result.Value);
        else if (outcome == "missing") Assert.IsType<NotFoundResult>(result.Result);
        else Assert.IsType<BadRequestResult>(result.Result);
        Assert.Empty(logger.Entries);
        service.Verify(value => value.GetSignedUrlAsync("bucket", "object", It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    private static ServiceProvider Provider(RecordingLogger logger) =>
        new ServiceCollection().AddSingleton<ILogger<SignedUrlController>>(logger).BuildServiceProvider();

    private static SignedUrlController Controller(IServiceProvider provider, IFileService service)
    {
        var controller = ActivatorUtilities.CreateInstance<SignedUrlController>(provider, service);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed record Entry(LogLevel Level, Exception? Exception, string Message, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<SignedUrlController>
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>();
            Entries.Add(new Entry(logLevel, exception, formatter(state, exception), properties));
        }
    }
}
