using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class InstantQuoteFileServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T12:00:00Z");

    [Fact]
    public async Task CreateSession_PersistsHashOnlyAndReturnsConfiguredCapability()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var owner = new InstantQuoteOwner("https://issuer.example|user-42", true);

        var response = await service.CreateInstantQuoteSessionAsync(owner, CancellationToken.None);

        var session = Assert.IsType<InstantQuoteUploadSession>(repository.CreatedSession);
        var tokenBytes = Base64UrlDecode(response.SessionToken);
        Assert.Equal(32, tokenBytes.Length);
        Assert.Equal(SHA256.HashData(tokenBytes), session.TokenHash);
        Assert.NotEqual(response.SessionToken, Encoding.UTF8.GetString(session.TokenHash));
        Assert.Equal(owner.PrincipalId, session.OwnerSubject);
        Assert.Equal(owner.IsAuthenticated, session.IsAuthenticated);
        Assert.Equal(Now.AddHours(24), response.ExpiresAt);
        Assert.Equal(response.SessionId, session.Id);
    }

    [Fact]
    public async Task Upload_ValidNonSeekableStl_ScansExactGenerationAndPersistsCleanWithObservedVersion()
    {
        var bytes = BinaryStl();
        var expectedSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var repository = new FakeRepository { VerifySessionResult = CreateSessionRecord() };
        var storage = new FakeStorage();
        var scanner = new FakeScanner();
        var service = CreateService(repository, storage, scanner);
        var owner = new InstantQuoteOwner("https://issuer.example|user-42", true);

        var response = await service.UploadAsync(
            repository.VerifySessionResult.Id,
            owner,
            new string('t', 43),
            new string('i', 16),
            expectedSha,
            new NonSeekableStream(bytes),
            new InstantQuoteUploadMetadata("customer-part.STL", "model/stl"),
            CancellationToken.None);

        Assert.Equal("clean", response.Status);
        Assert.Equal(expectedSha, response.Sha256);
        Assert.Equal(bytes.Length, response.SizeBytes);
        Assert.Equal(bytes, storage.UploadedBytes);
        Assert.Equal(bytes, scanner.ScannedBytes);
        Assert.Equal(1, storage.DownloadCount);
        Assert.Equal(2, repository.SavedUploads.Count);
        Assert.Equal(17U, repository.SavedUploads[0].ExpectedVersion);
        Assert.Equal(18U, repository.SavedUploads[1].ExpectedVersion);
        Assert.Equal(InstantQuoteWorkflowState.Clean, repository.SavedUploads[^1].Upload.State);
        Assert.Equal(storage.Metadata.Generation, repository.SavedUploads[^1].Upload.GcsGeneration);
    }

    [Theory]
    [InlineData(InstantQuoteReservationStatus.Conflict, typeof(InstantQuoteReplayConflictException))]
    [InlineData(InstantQuoteReservationStatus.InProgress, typeof(InstantQuoteUploadInProgressException))]
    public async Task Upload_NonAcquiredReservation_MapsStableFailure(
        InstantQuoteReservationStatus status,
        Type exceptionType)
    {
        var stored = CreateStoredUpload(InstantQuoteWorkflowState.Unknown);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            UploadReservation = new(status, stored, 17),
        };
        var storage = new FakeStorage();
        var service = CreateService(repository, storage);

        var exception = await Record.ExceptionAsync(() => service.UploadAsync(
            stored.SessionId,
            new InstantQuoteOwner("https://issuer.example|user-42", true),
            new string('t', 43),
            new string('i', 16),
            stored.ExpectedSha256,
            new MemoryStream(BinaryStl()),
            new InstantQuoteUploadMetadata(stored.OriginalFileName, stored.ValidatedContentType),
            CancellationToken.None));

        Assert.IsType(exceptionType, exception);
        Assert.Equal(0, storage.UploadCount);
    }

    [Fact]
    public async Task Upload_UnknownReservation_ReconcilesDeterministicTemporaryMetadataWithoutReupload()
    {
        var stored = CreateStoredUpload(InstantQuoteWorkflowState.Unknown);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            UploadReservation = new(InstantQuoteReservationStatus.Unknown, stored, 17),
        };
        var storage = new FakeStorage
        {
            ReconciliationMetadata = new InstantQuoteObjectMetadata(
                "private-bucket", stored.TemporaryObjectName, stored.GcsGeneration!.Value,
                stored.ActualSizeBytes!.Value, stored.ExpectedSha256),
        };
        storage.Seed(BinaryStl());
        var service = CreateService(repository, storage);

        var response = await service.UploadAsync(
            stored.SessionId,
            new InstantQuoteOwner("https://issuer.example|user-42", true),
            new string('t', 43),
            new string('i', 16),
            stored.ExpectedSha256,
            new MemoryStream(BinaryStl()),
            new InstantQuoteUploadMetadata(stored.OriginalFileName, stored.ValidatedContentType),
            CancellationToken.None);

        Assert.Equal("clean", response.Status);
        Assert.Equal(0, storage.UploadCount);
        Assert.Equal(1, storage.MetadataReadCount);
        Assert.Equal(1, storage.DownloadCount);
        Assert.Equal(17U, repository.SavedUploads[^1].ExpectedVersion);
    }

    [Fact]
    public async Task Upload_CompletedMatchingReservation_ReplaysWithoutStorageOrScanner()
    {
        var stored = CreateStoredUpload(InstantQuoteWorkflowState.Clean);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            UploadReservation = new(InstantQuoteReservationStatus.Replay, stored, 17),
        };
        var storage = new FakeStorage();
        var scanner = new FakeScanner();
        var service = CreateService(repository, storage, scanner);

        var response = await service.UploadAsync(
            stored.SessionId,
            new InstantQuoteOwner("https://issuer.example|user-42", true),
            new string('t', 43),
            new string('i', 16),
            stored.ExpectedSha256,
            new MemoryStream(BinaryStl()),
            new InstantQuoteUploadMetadata(stored.OriginalFileName, stored.ValidatedContentType),
            CancellationToken.None);

        Assert.Equal(stored.Id, response.FileId);
        Assert.Equal(0, storage.UploadCount);
        Assert.Empty(scanner.ScannedBytes);
    }

    [Fact]
    public async Task Finalize_CleanSessionFile_PromotesExactGenerationAndReturnsAuthoritativeLink()
    {
        var upload = CreateStoredUpload(InstantQuoteWorkflowState.Clean);
        var requestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            SessionFiles = [new InstantQuoteStoredUpload(upload, 23)],
        };
        var storage = new FakeStorage();
        var service = CreateService(repository, storage);

        var response = await service.FinalizeAsync(
            upload.SessionId,
            new InstantQuoteOwner("https://issuer.example|user-42", true),
            new string('t', 43),
            new string('k', 16),
            new FinalizeInstantQuoteFilesRequest(requestId, [upload.Id]),
            CancellationToken.None);

        var file = Assert.Single(response.Files);
        Assert.Equal("private-bucket", file.Bucket);
        Assert.Equal($"instant-quotation/{requestId:N}/{upload.Id:N}.stl", file.ObjectName);
        Assert.Equal(upload.GcsGeneration, storage.PromotedGeneration);
        Assert.Equal(23U, repository.SavedUploads[^1].ExpectedVersion);
        Assert.Equal(InstantQuoteWorkflowState.Finalized, upload.State);
        Assert.Equal(InstantQuoteWorkflowState.Finalized, repository.SavedFinalization?.Finalization.State);
    }

    [Theory]
    [InlineData(InstantQuoteScanResult.Unsafe, typeof(InstantQuoteUnsafeContentException))]
    [InlineData(InstantQuoteScanResult.Unavailable, typeof(InstantQuoteDependencyUnavailableException))]
    public async Task Upload_NonCleanScan_FailsClosedAndCleansExactGeneration(
        InstantQuoteScanResult result,
        Type exceptionType)
    {
        var bytes = BinaryStl();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var repository = new FakeRepository { VerifySessionResult = CreateSessionRecord() };
        var storage = new FakeStorage();
        var scanner = new FakeScanner { Result = result };
        var service = CreateService(repository, storage, scanner);

        var exception = await Record.ExceptionAsync(() => service.UploadAsync(
            repository.VerifySessionResult.Id,
            new InstantQuoteOwner("https://issuer.example|user-42", true),
            new string('t', 43),
            new string('i', 16),
            sha,
            new MemoryStream(bytes),
            new InstantQuoteUploadMetadata("part.stl", "model/stl"),
            CancellationToken.None));

        Assert.IsType(exceptionType, exception);
        Assert.Equal(1, storage.DeleteCount);
    }

    [Fact]
    public async Task Upload_ActualDigestMismatch_RejectsAndCleansBeforeScanning()
    {
        var bytes = BinaryStl();
        var serviceContext = CreateUploadContext();

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => serviceContext.Service.UploadAsync(
            serviceContext.Repository.VerifySessionResult!.Id,
            new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43), new string('i', 16),
            new string('a', 64), new MemoryStream(bytes), new InstantQuoteUploadMetadata("part.stl", "model/stl"),
            CancellationToken.None));

        Assert.Equal(1, serviceContext.Storage.DeleteCount);
        Assert.Empty(serviceContext.Scanner.ScannedBytes);
    }

    [Fact]
    public async Task Upload_ImplausibleSignature_RejectsAndCleansBeforeScanning()
    {
        var bytes = new byte[84];
        bytes[80] = 1;
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var serviceContext = CreateUploadContext();

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => serviceContext.Service.UploadAsync(
            serviceContext.Repository.VerifySessionResult!.Id,
            new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43), new string('i', 16),
            sha, new MemoryStream(bytes), new InstantQuoteUploadMetadata("part.stl", "model/stl"),
            CancellationToken.None));

        Assert.Equal(1, serviceContext.Storage.DeleteCount);
        Assert.Empty(serviceContext.Scanner.ScannedBytes);
    }

    [Fact]
    public async Task Upload_CancelledDuringScan_PersistsUnknownWithLatestXmin()
    {
        var bytes = BinaryStl();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var repository = new FakeRepository { VerifySessionResult = CreateSessionRecord() };
        var scanner = new FakeScanner { Exception = new OperationCanceledException() };
        var service = CreateService(repository, new FakeStorage(), scanner);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadAsync(
            repository.VerifySessionResult.Id,
            new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43), new string('i', 16),
            sha, new MemoryStream(bytes), new InstantQuoteUploadMetadata("part.stl", "model/stl"),
            CancellationToken.None));

        Assert.Equal(2, repository.SavedUploads.Count);
        Assert.Equal(18U, repository.SavedUploads[^1].ExpectedVersion);
        Assert.Equal(InstantQuoteWorkflowState.Unknown, repository.SavedUploads[^1].Upload.State);
    }

    [Fact]
    public async Task Upload_AmbiguousStorageFailure_PersistsUnknownAndReturnsOutcomeUnknown()
    {
        var bytes = BinaryStl();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var repository = new FakeRepository { VerifySessionResult = CreateSessionRecord() };
        var storage = new FakeStorage { UploadException = new IOException("ambiguous write") };

        await Assert.ThrowsAsync<InstantQuoteAmbiguousOutcomeException>(() =>
            CreateService(repository, storage).UploadAsync(
                repository.VerifySessionResult.Id,
                new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43), new string('i', 16),
                sha, new MemoryStream(bytes), new InstantQuoteUploadMetadata("part.stl", "model/stl"),
                CancellationToken.None));

        Assert.Equal(17U, repository.SavedUploads[^1].ExpectedVersion);
        Assert.Equal(InstantQuoteWorkflowState.Unknown, repository.SavedUploads[^1].Upload.State);
    }

    [Theory]
    [InlineData(InstantQuoteReservationStatus.Conflict, typeof(InstantQuoteReplayConflictException))]
    [InlineData(InstantQuoteReservationStatus.InProgress, typeof(InstantQuoteUploadInProgressException))]
    public async Task Finalize_NonAcquiredReservation_MapsStableFailure(
        InstantQuoteReservationStatus status,
        Type exceptionType)
    {
        var upload = CreateStoredUpload(InstantQuoteWorkflowState.Clean);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            FinalizationStatus = status,
            SessionFiles = [new InstantQuoteStoredUpload(upload, 23)],
        };
        var service = CreateService(repository);

        var exception = await Record.ExceptionAsync(() => service.FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [upload.Id]),
            CancellationToken.None));

        Assert.IsType(exceptionType, exception);
    }

    [Theory]
    [InlineData(InstantQuoteReservationStatus.Replay)]
    [InlineData(InstantQuoteReservationStatus.Unknown)]
    public async Task Finalize_ReplayOrUnknownFinalizedFile_ReconcilesAuthoritativeLinkWithoutPromotion(
        InstantQuoteReservationStatus status)
    {
        var upload = CreateStoredUpload(InstantQuoteWorkflowState.Finalized);
        upload.FinalObjectName = $"instant-quotation/{Guid.NewGuid():N}/{upload.Id:N}.stl";
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            FinalizationStatus = status,
            SessionFiles = [new InstantQuoteStoredUpload(upload, 23)],
        };
        var storage = new FakeStorage
        {
            ReconciliationMetadata = new InstantQuoteObjectMetadata(
                "private-bucket", upload.FinalObjectName, 202, upload.ActualSizeBytes!.Value, upload.ActualSha256!),
        };

        var response = await CreateService(repository, storage).FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [upload.Id]), CancellationToken.None);

        Assert.Equal(upload.FinalObjectName, Assert.Single(response.Files).ObjectName);
        Assert.Equal(0, storage.PromotionCount);
    }

    [Fact]
    public async Task Finalize_PartialRetry_ReusesFinalizedLinkAndPromotesOnlyRemainingCleanFile()
    {
        var finalized = CreateStoredUpload(InstantQuoteWorkflowState.Finalized);
        finalized.FinalObjectName = $"instant-quotation/{Guid.NewGuid():N}/{finalized.Id:N}.stl";
        var clean = new InstantQuoteUploadFile(
            Guid.Parse("44444444-4444-4444-4444-444444444444"), finalized.SessionId,
            SHA256.HashData(Encoding.UTF8.GetBytes("other-idempotency")), new string('e', 64), "other.stl", ".stl",
            "model/stl", finalized.ExpectedSha256, finalized.ActualSha256, finalized.ActualSizeBytes,
            303, "instant-quotation/temp/other.stl", null, InstantQuoteWorkflowState.Clean, Now, Now);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            SessionFiles = [new InstantQuoteStoredUpload(finalized, 23), new InstantQuoteStoredUpload(clean, 24)],
        };
        var storage = new FakeStorage
        {
            ReconciliationMetadata = new InstantQuoteObjectMetadata(
                "private-bucket", finalized.FinalObjectName, 202, finalized.ActualSizeBytes!.Value, finalized.ActualSha256!),
        };

        var response = await CreateService(repository, storage).FinalizeAsync(
            finalized.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [finalized.Id, clean.Id]),
            CancellationToken.None);

        Assert.Equal(2, response.Files.Count);
        Assert.Equal(1, storage.PromotionCount);
        Assert.Single(repository.SavedUploads);
        Assert.Equal(clean.Id, repository.SavedUploads[0].Upload.Id);
    }

    [Fact]
    public async Task Finalize_EmptyDuplicateOrMixedSessionSelection_IsRejected()
    {
        var upload = CreateStoredUpload(InstantQuoteWorkflowState.Clean);
        var repository = new FakeRepository { VerifySessionResult = CreateSessionRecord(), SessionFiles = [] };
        var service = CreateService(repository);

        await Assert.ThrowsAsync<InstantQuoteValidationException>(() => service.FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.Empty, []), CancellationToken.None));
        await Assert.ThrowsAsync<InstantQuoteValidationException>(() => service.FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [upload.Id, upload.Id]), CancellationToken.None));
        await Assert.ThrowsAsync<InstantQuoteOwnershipException>(() => service.FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [upload.Id]), CancellationToken.None));
    }

    [Fact]
    public async Task Finalize_NotCleanSelection_IsRejected()
    {
        var upload = CreateStoredUpload(InstantQuoteWorkflowState.Failed);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            SessionFiles = [new InstantQuoteStoredUpload(upload, 23)],
        };

        await Assert.ThrowsAsync<InstantQuoteOwnershipException>(() => CreateService(repository).FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [upload.Id]), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Finalize_PromotionOrStateSaveAmbiguity_PersistsUnknown(
        bool promotionFails,
        bool stateSaveFails)
    {
        var upload = CreateStoredUpload(InstantQuoteWorkflowState.Clean);
        var repository = new FakeRepository
        {
            VerifySessionResult = CreateSessionRecord(),
            SessionFiles = [new InstantQuoteStoredUpload(upload, 23)],
            SaveUploadException = stateSaveFails ? new IOException("save failed") : null,
        };
        var storage = new FakeStorage { PromotionException = promotionFails ? new IOException("copy failed") : null };

        await Assert.ThrowsAsync<InstantQuoteAmbiguousOutcomeException>(() => CreateService(repository, storage).FinalizeAsync(
            upload.SessionId, new InstantQuoteOwner("https://issuer.example|user-42", true), new string('t', 43),
            new string('k', 16), new FinalizeInstantQuoteFilesRequest(Guid.NewGuid(), [upload.Id]), CancellationToken.None));

        Assert.Equal(InstantQuoteWorkflowState.Unknown, repository.SavedFinalization?.Finalization.State);
    }

    private static InstantQuoteFileService CreateService(
        FakeRepository repository,
        FakeStorage? storage = null,
        FakeScanner? scanner = null) => new(
        repository,
        storage ?? new FakeStorage(),
        scanner ?? new FakeScanner(),
        Options.Create(new InstantQuoteFileOptions()),
        new FakeTimeProvider(Now));

    private static (InstantQuoteFileService Service, FakeRepository Repository, FakeStorage Storage, FakeScanner Scanner)
        CreateUploadContext()
    {
        var repository = new FakeRepository { VerifySessionResult = CreateSessionRecord() };
        var storage = new FakeStorage();
        var scanner = new FakeScanner();
        return (CreateService(repository, storage, scanner), repository, storage, scanner);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static byte[] BinaryStl()
    {
        var bytes = new byte[84];
        Encoding.ASCII.GetBytes("binary stl").CopyTo(bytes, 0);
        return bytes;
    }

    private static InstantQuoteUploadSession CreateSessionRecord() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "https://issuer.example|user-42",
        true,
        SHA256.HashData(Encoding.UTF8.GetBytes(new string('t', 43))),
        Now.AddHours(1),
        Now);

    private static InstantQuoteUploadFile CreateStoredUpload(InstantQuoteWorkflowState state)
    {
        var bytes = BinaryStl();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new InstantQuoteUploadFile(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SHA256.HashData(Encoding.UTF8.GetBytes(new string('i', 16))),
            new string('f', 64), "customer-part.STL", ".stl", "model/stl", sha,
            sha, bytes.Length, 101, "instant-quotation/temp/opaque.stl", null, state, Now, Now);
    }

    private sealed class FakeRepository : IInstantQuoteFileRepository
    {
        public InstantQuoteUploadSession? CreatedSession { get; private set; }
        public InstantQuoteUploadSession? VerifySessionResult { get; init; }
        public List<(InstantQuoteUploadFile Upload, uint ExpectedVersion)> SavedUploads { get; } = [];
        public InstantQuoteReservation<InstantQuoteUploadFile>? UploadReservation { get; init; }
        public IReadOnlyList<InstantQuoteStoredUpload> SessionFiles { get; init; } = [];
        public (InstantQuoteFinalization Finalization, uint ExpectedVersion)? SavedFinalization { get; private set; }
        public InstantQuoteReservationStatus FinalizationStatus { get; init; } = InstantQuoteReservationStatus.Acquired;
        public Exception? SaveUploadException { get; init; }

        public Task CreateSessionAsync(InstantQuoteUploadSession session, CancellationToken cancellationToken)
        {
            CreatedSession = session;
            return Task.CompletedTask;
        }

        public Task<InstantQuoteUploadSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<InstantQuoteUploadSession?>(null);

        public Task<InstantQuoteUploadSession?> VerifySessionAsync(Guid sessionId, byte[] tokenHash, string? ownerSubject,
            bool isAuthenticated, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(VerifySessionResult);

        public Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(
            InstantQuoteUploadFile upload, CancellationToken cancellationToken) =>
            Task.FromResult(UploadReservation ?? new InstantQuoteReservation<InstantQuoteUploadFile>(
                InstantQuoteReservationStatus.Acquired, upload, 17));

        public Task<uint> SaveUploadAsync(InstantQuoteUploadFile upload, uint expectedVersion,
            CancellationToken cancellationToken)
        {
            if (SaveUploadException is not null)
            {
                throw SaveUploadException;
            }
            SavedUploads.Add((upload, expectedVersion));
            return Task.FromResult(expectedVersion + 1);
        }

        public Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationAsync(
            InstantQuoteFinalization finalization, CancellationToken cancellationToken) =>
            Task.FromResult(new InstantQuoteReservation<InstantQuoteFinalization>(
                FinalizationStatus, finalization, 31));

        public Task<uint> SaveFinalizationAsync(InstantQuoteFinalization finalization, uint expectedVersion,
            CancellationToken cancellationToken)
        {
            SavedFinalization = (finalization, expectedVersion);
            return Task.FromResult(expectedVersion + 1);
        }

        public Task<IReadOnlyList<InstantQuoteStoredUpload>> GetSessionFilesAsync(Guid sessionId,
            IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) => Task.FromResult(SessionFiles);
    }

    private sealed class FakeStorage : IInstantQuoteObjectStorage
    {
        private byte[] storedBytes = [];
        public byte[] UploadedBytes => storedBytes;
        public int UploadCount { get; private set; }
        public int DownloadCount { get; private set; }
        public long? PromotedGeneration { get; private set; }
        public int DeleteCount { get; private set; }
        public int MetadataReadCount { get; private set; }
        public int PromotionCount { get; private set; }
        public InstantQuoteObjectMetadata? ReconciliationMetadata { get; init; }
        public Exception? PromotionException { get; init; }
        public Exception? UploadException { get; init; }
        public InstantQuoteObjectMetadata Metadata { get; private set; } = new(
            "private-bucket", "", 101, 0, new string('0', 64));

        public async Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(string objectName, Stream content,
            string expectedSha256, CancellationToken cancellationToken)
        {
            UploadCount++;
            if (UploadException is not null)
            {
                throw UploadException;
            }
            await using var destination = new MemoryStream();
            await content.CopyToAsync(destination, cancellationToken);
            storedBytes = destination.ToArray();
            Metadata = new("private-bucket", objectName, 101, storedBytes.Length, expectedSha256);
            return Metadata;
        }

        public Task<InstantQuoteObjectMetadata?> GetMetadataAsync(string bucket, string objectName,
            CancellationToken cancellationToken)
        {
            MetadataReadCount++;
            return Task.FromResult(ReconciliationMetadata);
        }

        public void Seed(byte[] bytes) => storedBytes = bytes;

        public async Task DownloadGenerationAsync(string bucket, string objectName, long generation, Stream destination,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            await destination.WriteAsync(storedBytes, cancellationToken);
        }

        public Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(string sourceBucket, string sourceObjectName,
            long sourceGeneration, string destinationObjectName, CancellationToken cancellationToken)
        {
            if (PromotionException is not null)
            {
                throw PromotionException;
            }
            PromotionCount++;
            PromotedGeneration = sourceGeneration;
            return Task.FromResult(new InstantQuoteObjectMetadata(
                "private-bucket", destinationObjectName, 202, storedBytes.Length, Metadata.Sha256));
        }

        public Task DeleteGenerationAsync(string bucket, string objectName, long generation,
            CancellationToken cancellationToken)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScanner : IInstantQuoteFileSafetyScanner
    {
        public byte[] ScannedBytes { get; private set; } = [];
        public InstantQuoteScanResult Result { get; init; } = InstantQuoteScanResult.Clean;
        public Exception? Exception { get; init; }

        public async Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            if (Exception is not null)
            {
                throw Exception;
            }
            await using var destination = new MemoryStream();
            await content.CopyToAsync(destination, cancellationToken);
            ScannedBytes = destination.ToArray();
            return Result;
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
