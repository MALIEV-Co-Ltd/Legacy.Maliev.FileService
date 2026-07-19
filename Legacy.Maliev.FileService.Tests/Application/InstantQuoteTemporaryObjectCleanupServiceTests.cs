using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class InstantQuoteTemporaryObjectCleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnce_CleanupDisabled_PerformsNoRepositoryOrStorageWork()
    {
        var repository = new FakeCleanupRepository();
        var storage = new FakeStorage();
        var service = CreateService(repository, storage, cleanupEnabled: false);

        var cleaned = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, cleaned);
        Assert.Equal(0, repository.QueryCount);
        Assert.Empty(storage.Deletes);
    }

    [Fact]
    public async Task RunOnce_ExpiredCleanUpload_ClaimsRemovedThenDeletesExactGeneration()
    {
        var upload = CreateUpload(InstantQuoteWorkflowState.Clean);
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage();

        var cleaned = await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.Equal(2, repository.Saves.Count);
        Assert.Equal(InstantQuoteWorkflowState.Removed, repository.Saves[0].State);
        Assert.Equal(17U, repository.Saves[0].ExpectedVersion);
        Assert.Equal(("temporary-bucket", upload.TemporaryObjectName, 42L), Assert.Single(storage.Deletes));
        Assert.Null(repository.Saves[1].Generation);
        Assert.True(repository.Saves[1].TemporaryCleanupCompleted);
        Assert.Equal(18U, repository.Saves[1].ExpectedVersion);
    }

    [Fact]
    public async Task RunOnce_DeleteFails_RetainsRemovedStateAndGenerationForRetry()
    {
        var upload = CreateUpload(InstantQuoteWorkflowState.Clean);
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage { DeleteException = new IOException("storage unavailable") };

        var cleaned = await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, cleaned);
        var claimed = Assert.Single(repository.Saves);
        Assert.Equal(InstantQuoteWorkflowState.Removed, claimed.State);
        Assert.Equal(42, claimed.Generation);
    }

    [Fact]
    public async Task RunOnce_StalePendingWithoutObject_PersistsTerminalFailure()
    {
        var upload = CreateUpload(InstantQuoteWorkflowState.Pending);
        upload.GcsGeneration = null;
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage();

        var cleaned = await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.Equal(2, repository.Saves.Count);
        Assert.Equal(InstantQuoteWorkflowState.Failed, repository.Saves[^1].State);
        Assert.True(repository.Saves[^1].TemporaryCleanupCompleted);
        Assert.Empty(storage.Deletes);
    }

    [Fact]
    public async Task RunOnce_RejectedUploadWithoutObject_MarksCleanupCompleteAndPreservesReplayState()
    {
        var upload = CreateUpload(InstantQuoteWorkflowState.PayloadTooLarge);
        upload.GcsGeneration = null;
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage();

        var cleaned = await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.Equal(InstantQuoteWorkflowState.PayloadTooLarge, upload.State);
        Assert.True(upload.TemporaryCleanupCompleted);
        Assert.Empty(storage.Deletes);
    }

    [Fact]
    public async Task RunOnce_RejectedUploadMetadataFailure_RetainsIncompleteCleanupForRetry()
    {
        var upload = CreateUpload(InstantQuoteWorkflowState.InvalidRequest);
        upload.GcsGeneration = null;
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage { MetadataException = new IOException("metadata unavailable") };

        var cleaned = await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, cleaned);
        Assert.False(upload.TemporaryCleanupCompleted);
        Assert.Single(repository.Saves);
    }

    [Theory]
    [InlineData(InstantQuoteWorkflowState.Pending)]
    [InlineData(InstantQuoteWorkflowState.Uploaded)]
    [InlineData(InstantQuoteWorkflowState.Unknown)]
    public async Task RunOnce_StaleRecoverableObject_ReconcilesExactGenerationToClean(
        InstantQuoteWorkflowState state)
    {
        var bytes = new byte[84];
        System.Text.Encoding.ASCII.GetBytes("binary stl").CopyTo(bytes, 0);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var upload = CreateUpload(state, sha);
        upload.ActualSha256 = null;
        upload.ActualSizeBytes = null;
        upload.GcsGeneration = null;
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage(bytes, new InstantQuoteObjectMetadata(
            upload.TemporaryBucket, upload.TemporaryObjectName, 73, bytes.Length, sha));

        var cleaned = await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, cleaned);
        Assert.Equal(2, repository.Saves.Count);
        var reconciled = repository.Saves[^1];
        Assert.Equal(InstantQuoteWorkflowState.Clean, reconciled.State);
        Assert.Equal(73, reconciled.Generation);
        Assert.Empty(storage.Deletes);
    }

    [Fact]
    public async Task RunOnce_FinalizedUpload_DeletesOnlyTemporaryGenerationAndPreservesAuthority()
    {
        var upload = CreateUpload(InstantQuoteWorkflowState.Finalized);
        upload.FinalBucket = "final-bucket";
        upload.FinalObjectName = "instant-quotation/1001/final.stl";
        upload.FinalizedQuotationRequestId = 1001;
        var repository = new FakeCleanupRepository(new InstantQuoteStoredUpload(upload, 17));
        var storage = new FakeStorage();

        await CreateService(repository, storage).RunOnceAsync(CancellationToken.None);

        Assert.Equal((upload.TemporaryBucket, upload.TemporaryObjectName, 42L), Assert.Single(storage.Deletes));
        var completed = repository.Saves[^1];
        Assert.Equal(InstantQuoteWorkflowState.Finalized, completed.State);
        Assert.True(upload.TemporaryCleanupCompleted);
        Assert.Equal("final-bucket", upload.FinalBucket);
        Assert.Equal("instant-quotation/1001/final.stl", upload.FinalObjectName);
        Assert.Equal(1001, upload.FinalizedQuotationRequestId);
    }

    [Fact]
    public async Task RunOnce_MultipleCandidates_StampsEachClaimAtItsActualStartTime()
    {
        var first = CreateUpload(InstantQuoteWorkflowState.Finalized);
        var second = CreateUpload(InstantQuoteWorkflowState.Finalized);
        var repository = new FakeCleanupRepository(
            new InstantQuoteStoredUpload(first, 17),
            new InstantQuoteStoredUpload(second, 27));
        var time = new FakeTimeProvider(Now);
        var storage = new FakeStorage
        {
            AfterDelete = () => time.Advance(TimeSpan.FromMinutes(2)),
        };

        await CreateService(repository, storage, timeProvider: time).RunOnceAsync(CancellationToken.None);

        var claims = repository.Saves.Where(value => value.Generation is not null).ToArray();
        Assert.Equal(2, claims.Length);
        Assert.Equal(Now, claims[0].ModifiedAt);
        Assert.Equal(Now.AddMinutes(2), claims[1].ModifiedAt);
    }

    private static InstantQuoteTemporaryObjectCleanupService CreateService(
        FakeCleanupRepository repository,
        FakeStorage storage,
        bool cleanupEnabled = true,
        FakeTimeProvider? timeProvider = null) => new(
            repository,
            storage,
            new CleanScanner(),
            Options.Create(new InstantQuoteFileOptions
            {
                Enabled = true,
                WritesEnabled = true,
                CleanupEnabled = cleanupEnabled,
                CleanupBatchSize = 25,
                CleanupRetryDelay = TimeSpan.FromMinutes(5),
                CleanupSessionExpiryGrace = TimeSpan.FromMinutes(15),
                CleanupTimeout = TimeSpan.FromSeconds(5),
            }),
            timeProvider ?? new FakeTimeProvider(Now),
            NullLogger<InstantQuoteTemporaryObjectCleanupService>.Instance);

    private static InstantQuoteUploadFile CreateUpload(InstantQuoteWorkflowState state, string? expectedSha256 = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), new byte[32], new string('a', 64), "part.stl", ".stl", "model/stl",
        expectedSha256 ?? new string('b', 64), expectedSha256 ?? new string('b', 64), 128, 42, "temporary-bucket",
        $"instant-quotation/temp/{Guid.NewGuid():N}.stl", null, null, state, Now.AddHours(-2), Now.AddHours(-1));

    private sealed class FakeCleanupRepository(params InstantQuoteStoredUpload[] candidates)
        : IInstantQuoteCleanupRepository
    {
        public int QueryCount { get; private set; }
        public List<(InstantQuoteWorkflowState State, long? Generation, bool TemporaryCleanupCompleted,
            DateTimeOffset ModifiedAt, uint ExpectedVersion)> Saves
        { get; } = [];

        public Task<IReadOnlyList<InstantQuoteStoredUpload>> GetTemporaryCleanupCandidatesAsync(
            DateTimeOffset expiredBefore,
            DateTimeOffset retryBefore,
            int batchSize,
            CancellationToken cancellationToken)
        {
            QueryCount++;
            return Task.FromResult<IReadOnlyList<InstantQuoteStoredUpload>>(candidates);
        }

        public Task<uint> SaveCleanupStateAsync(
            InstantQuoteUploadFile upload,
            uint expectedVersion,
            CancellationToken cancellationToken)
        {
            Saves.Add((upload.State, upload.GcsGeneration, upload.TemporaryCleanupCompleted,
                upload.ModifiedAt, expectedVersion));
            return Task.FromResult(expectedVersion + 1);
        }
    }

    private sealed class FakeStorage(
        byte[]? bytes = null,
        InstantQuoteObjectMetadata? metadata = null) : IInstantQuoteObjectStorage
    {
        public Exception? DeleteException { get; init; }
        public Exception? MetadataException { get; init; }
        public Action? AfterDelete { get; init; }
        public List<(string Bucket, string ObjectName, long Generation)> Deletes { get; } = [];

        public Task DeleteGenerationAsync(string bucket, string objectName, long generation, CancellationToken cancellationToken)
        {
            Deletes.Add((bucket, objectName, generation));
            if (DeleteException is not null)
            {
                return Task.FromException(DeleteException);
            }
            AfterDelete?.Invoke();
            return Task.CompletedTask;
        }

        public Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(string bucket, string objectName, Stream content,
            string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstantQuoteObjectMetadata?> GetMetadataAsync(string bucket, string objectName,
            CancellationToken cancellationToken) => MetadataException is null
                ? Task.FromResult(metadata)
                : Task.FromException<InstantQuoteObjectMetadata?>(MetadataException);
        public async Task DownloadGenerationAsync(string bucket, string objectName, long generation, Stream destination,
            CancellationToken cancellationToken)
        {
            if (bytes is not null)
            {
                await destination.WriteAsync(bytes, cancellationToken);
            }
        }
        public Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(string sourceBucket, string sourceObjectName,
            long sourceGeneration, string destinationBucket, string destinationObjectName,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CleanScanner : IInstantQuoteFileSafetyScanner
    {
        public async Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            return InstantQuoteScanResult.Clean;
        }
    }
}
