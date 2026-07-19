using System.Security.Claims;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Tests.Api;

public sealed class RuntimeSafetyTests
{
    [Fact]
    public void Options_DefaultsAreWriteDisabledAndBucketless()
    {
        var instant = new InstantQuoteFileOptions();
        var legacy = new FileStorageOptions();

        Assert.False(instant.Enabled);
        Assert.False(instant.WritesEnabled);
        Assert.False(instant.CleanupEnabled);
        Assert.Empty(instant.TemporaryBucket);
        Assert.Empty(instant.FinalBucket);
        Assert.False(legacy.Enabled);
        Assert.False(legacy.WritesEnabled);
        Assert.Empty(legacy.AllowedBuckets);
    }

    [Fact]
    public void AddFileServiceRuntime_CleanupDisabled_RegistersNoHostedWriter()
    {
        using var provider = CreateServices(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "true"),
            new("InstantQuoteFiles:CleanupEnabled", "false"),
            new("InstantQuoteFiles:TemporaryBucket", "quote-temp-local"),
            new("InstantQuoteFiles:FinalBucket", "quote-final-local"),
        ], new RecordingInstantQuoteRepository()).BuildServiceProvider();

        Assert.DoesNotContain(provider.GetServices<IHostedService>(),
            service => service is InstantQuoteTemporaryObjectCleanupHostedService);
    }

    [Fact]
    public async Task AddFileServiceRuntime_DisabledConfiguration_ResolvesWithoutAdcAndFailsBeforePersistence()
    {
        var repository = new RecordingInstantQuoteRepository();
        await using var provider = CreateServices([], repository).BuildServiceProvider();

        Assert.Null(provider.GetService<StorageClient>());
        Assert.IsType<SingleFileMultipartReader>(provider.GetRequiredService<IInstantQuoteMultipartReader>());
        var service = provider.GetRequiredService<IInstantQuoteFileService>();

        var exception = await Assert.ThrowsAsync<InstantQuoteDependencyUnavailableException>(() =>
            service.CreateInstantQuoteSessionAsync(new InstantQuoteOwner("owner", true), CancellationToken.None));

        Assert.Equal("Instant quotation file writes are disabled.", exception.Message);
        Assert.Equal(0, repository.CreateSessionCalls);
    }

    [Fact]
    public async Task CreateSession_DisabledConfiguration_ReturnsStableDependencyUnavailableProblem()
    {
        await using var provider = CreateServices([], new RecordingInstantQuoteRepository()).BuildServiceProvider();
        var controller = new InstantQuotationFilesController(
            provider.GetRequiredService<IInstantQuoteFileService>(),
            provider.GetRequiredService<IInstantQuoteMultipartReader>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("iss", "https://issuer.test"),
                        new Claim("sub", "user-42"),
                    ], "test")),
                },
            },
        };

        var action = await controller.CreateSessionAsync(CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("dependency_unavailable", problem.Extensions["code"]);
    }

    [Fact]
    public async Task AddFileServiceRuntime_DevelopmentConfiguration_CannotWriteLegacyOrInstantQuoteData()
    {
        var repository = new RecordingInstantQuoteRepository();
        await using var provider = CreateServices(
        [
            new("FileStorage:WritesEnabled", "false"),
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "false"),
        ], repository).BuildServiceProvider();

        var instant = provider.GetRequiredService<IInstantQuoteFileService>();
        await Assert.ThrowsAsync<InstantQuoteDependencyUnavailableException>(() =>
            instant.CreateInstantQuoteSessionAsync(new InstantQuoteOwner("owner", true), CancellationToken.None));
        Assert.Equal(0, repository.CreateSessionCalls);

        var legacy = provider.GetRequiredService<IFileService>();
        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => legacy.UploadAsync(
            "bucket",
            null,
            [new MemoryUploadFile()],
            CancellationToken.None));
    }

    [Fact]
    public void AddFileServiceRuntime_LegacyEnabledFalseWritesTrue_DoesNotRegisterAdcOrRealBoundaries()
    {
        using var provider = CreateServices(
        [
            new("FileStorage:Enabled", "false"),
            new("FileStorage:WritesEnabled", "true"),
        ], new RecordingInstantQuoteRepository()).BuildServiceProvider();

        Assert.Null(provider.GetService<StorageClient>());
        Assert.IsType<DisabledObjectStorage>(provider.GetRequiredService<IObjectStorage>());
        Assert.IsType<DisabledFileSafetyScanner>(provider.GetRequiredService<IFileSafetyScanner>());
    }

    [Fact]
    public void AddFileServiceRuntime_EnabledConfiguration_UsesInjectedBoundariesWithoutResolvingAdc()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IInstantQuoteFileRepository, RecordingInstantQuoteRepository>();
        services.AddSingleton<IInstantQuoteObjectStorage, RecordingInstantQuoteStorage>();
        services.AddSingleton<IInstantQuoteFileSafetyScanner, CleanInstantQuoteScanner>();
        services.AddFileServiceRuntime(BuildConfiguration(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "true"),
            new("InstantQuoteFiles:TemporaryBucket", "quote-temp-local"),
            new("InstantQuoteFiles:FinalBucket", "quote-final-local"),
        ]));
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IInstantQuoteFileService>());
        Assert.IsType<RecordingInstantQuoteStorage>(provider.GetRequiredService<IInstantQuoteObjectStorage>());
        Assert.IsType<CleanInstantQuoteScanner>(provider.GetRequiredService<IInstantQuoteFileSafetyScanner>());
    }

    [Fact]
    public void InstantQuoteOptionsValidator_EnabledWritesRequireDistinctBucketsAndSafeLifetimes()
    {
        using var missingBuckets = CreateServices(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "true"),
        ], new RecordingInstantQuoteRepository()).BuildServiceProvider();
        using var sameBuckets = CreateServices(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "true"),
            new("InstantQuoteFiles:TemporaryBucket", "same"),
            new("InstantQuoteFiles:FinalBucket", "same"),
        ], new RecordingInstantQuoteRepository()).BuildServiceProvider();
        using var unsafeLifetimes = CreateServices(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:SessionLifetime", "00:00:01"),
            new("InstantQuoteFiles:CleanupTimeout", "00:10:00"),
        ], new RecordingInstantQuoteRepository()).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            missingBuckets.GetRequiredService<IOptions<InstantQuoteFileOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() =>
            sameBuckets.GetRequiredService<IOptions<InstantQuoteFileOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() =>
            unsafeLifetimes.GetRequiredService<IOptions<InstantQuoteFileOptions>>().Value);
    }

    [Theory]
    [InlineData("00:00:15")]
    [InlineData("00:00:10")]
    public void InstantQuoteOptionsValidator_CleanupRetryMustSafelyExceedDeleteTimeout(string retryDelay)
    {
        using var provider = CreateServices(
        [
            new("InstantQuoteFiles:Enabled", "true"),
            new("InstantQuoteFiles:WritesEnabled", "true"),
            new("InstantQuoteFiles:CleanupEnabled", "true"),
            new("InstantQuoteFiles:TemporaryBucket", "quote-temp-local"),
            new("InstantQuoteFiles:FinalBucket", "quote-final-local"),
            new("InstantQuoteFiles:CleanupTimeout", "00:00:15"),
            new("InstantQuoteFiles:CleanupRetryDelay", retryDelay),
        ], new RecordingInstantQuoteRepository()).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<InstantQuoteFileOptions>>().Value);
    }

    private static IServiceCollection CreateServices(
        IReadOnlyCollection<KeyValuePair<string, string?>> values,
        RecordingInstantQuoteRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IInstantQuoteFileRepository>(repository);
        services.AddSingleton<IUploadRepository, UnusedUploadRepository>();
        services.AddFileServiceRuntime(BuildConfiguration(values));
        return services;
    }

    private static IConfiguration BuildConfiguration(IReadOnlyCollection<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class MemoryUploadFile : IUploadFile
    {
        public string FileName => "part.stl";
        public string ContentType => "model/stl";
        public long Length => 1;
        public Stream OpenReadStream() => new MemoryStream([0]);
    }

    private sealed class UnusedUploadRepository : IUploadRepository
    {
        public Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled write gate must run before persistence.");

        public Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled write gate must run before persistence.");

        public Task DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled write gate must run before persistence.");

        public Task MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled write gate must run before persistence.");
    }

    private sealed class RecordingInstantQuoteRepository : IInstantQuoteFileRepository
    {
        public int CreateSessionCalls { get; private set; }

        public Task CreateSessionAsync(InstantQuoteUploadSession session, CancellationToken cancellationToken)
        {
            CreateSessionCalls++;
            return Task.CompletedTask;
        }

        public Task<InstantQuoteUploadSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InstantQuoteUploadSession?> VerifySessionAsync(
            Guid sessionId,
            byte[] tokenHash,
            string? ownerSubject,
            bool isAuthenticated,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(
            InstantQuoteUploadFile upload,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<uint> SaveUploadAsync(
            InstantQuoteUploadFile upload,
            uint expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InstantQuoteStoredUpload>> GetSessionFilesAsync(
            Guid sessionId,
            IReadOnlyCollection<Guid> fileIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationAsync(
            InstantQuoteFinalization finalization,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<uint> SaveFinalizationAsync(
            InstantQuoteFinalization finalization,
            uint expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingInstantQuoteStorage : IInstantQuoteObjectStorage
    {
        public Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(string bucket, string objectName, Stream content, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstantQuoteObjectMetadata?> GetMetadataAsync(string bucket, string objectName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadGenerationAsync(string bucket, string objectName, long generation, Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(string sourceBucket, string sourceObjectName, long sourceGeneration, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteGenerationAsync(string bucket, string objectName, long generation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CleanInstantQuoteScanner : IInstantQuoteFileSafetyScanner
    {
        public Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(InstantQuoteScanResult.Clean);
    }
}
