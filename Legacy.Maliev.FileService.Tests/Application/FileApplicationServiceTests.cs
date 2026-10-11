using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class FileApplicationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task UploadAsync_CleanFile_QuarantinesScansPromotesRecordsAndSigns()
    {
        var storage = new RecordingStorage();
        var scanner = new StubScanner(new FileSafetyResult(FileSafetyVerdict.Clean));
        var repository = new RecordingRepository();
        var service = CreateService(storage, scanner, repository);

        var result = await service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("MODEL.STL", "model/stl", [1, 2, 3])],
            CancellationToken.None);

        var uploaded = Assert.Single(storage.Uploaded);
        Assert.StartsWith("_quarantine/", uploaded.ObjectName, StringComparison.Ordinal);
        var move = Assert.Single(storage.Moved);
        Assert.Equal(uploaded.ObjectName, move.SourceObjectName);
        Assert.Matches(@"^uploads/2026-7-15/[0-9a-f-]+/model\.stl$", move.DestinationObjectName);
        var metadata = Assert.Single(repository.Uploads);
        Assert.Equal(move.DestinationObjectName, metadata.Name);
        var response = Assert.Single(result.Object);
        Assert.Equal(metadata.Name, response.ObjectName);
        Assert.Equal(new Uri($"https://storage.test/{metadata.Name}"), response.Uri);
    }

    [Fact]
    public async Task UploadAsync_InfectedFile_FailsClosedAndDeletesQuarantine()
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository();
        var service = CreateService(
            storage,
            new StubScanner(new FileSafetyResult(FileSafetyVerdict.Infected, "Eicar-Test-Signature")),
            repository);

        await Assert.ThrowsAsync<MalwareDetectedException>(() => service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("bad.bin", "application/octet-stream", [1])],
            CancellationToken.None));

        Assert.Empty(storage.Moved);
        Assert.Single(storage.Deleted);
        Assert.Equal(17, Assert.Single(storage.DeletedGenerations));
        Assert.Empty(repository.Uploads);
    }

    [Fact]
    public async Task UploadAsync_ScannerUnavailable_FailsClosedAndDeletesQuarantine()
    {
        var storage = new RecordingStorage();
        var service = CreateService(
            storage,
            new StubScanner(new FileSafetyResult(FileSafetyVerdict.Unavailable)),
            new RecordingRepository());

        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => service.UploadAsync(
            "maliev.com",
            "uploads/customer",
            [new MemoryUploadFile("part.step", "application/step", [1])],
            CancellationToken.None));

        Assert.Empty(storage.Moved);
        Assert.Single(storage.Deleted);
        Assert.Equal(17, Assert.Single(storage.DeletedGenerations));
    }

    [Fact]
    public async Task UploadAsync_MoveOutcomeUnknown_PreservesQuarantineAndDoesNotPersistMetadata()
    {
        var uncertain = new UploadOutcomeUnknownException("Storage move outcome requires reconciliation.");
        var storage = new RecordingStorage { MoveFailure = uncertain };
        var repository = new RecordingRepository();
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync(
            "maliev.com", null, [new MemoryUploadFile("part.step", "application/step", [1])],
            CancellationToken.None));

        Assert.Same(uncertain, failure);
        Assert.Single(storage.Uploaded);
        Assert.Single(storage.Moved);
        Assert.Empty(storage.Deleted);
        Assert.Empty(repository.Uploads);
    }

    [Fact]
    public async Task UploadAsync_JournaledMoveReturnsFalse_PreservesQuarantineWithoutNameCleanup()
    {
        var storage = new RecordingStorage { MoveResult = false };
        var repository = new RecordingRepository();
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync(
            "maliev.com", null, [new MemoryUploadFile("part.step", "application/step", [1])], default));

        Assert.Single(storage.Uploaded);
        Assert.Empty(storage.Deleted);
        Assert.Empty(storage.DeletedGenerations);
        Assert.Empty(repository.Uploads);
    }

    [Fact]
    public async Task UploadAsync_KnownFailureUsesIndependentCleanupToken()
    {
        using var request = new CancellationTokenSource();
        var storage = new RecordingStorage();
        var service = CreateService(
            storage,
            new StubScanner(new FileSafetyResult(FileSafetyVerdict.Unavailable), request.Cancel),
            new RecordingRepository());

        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("part.step", "application/step", [1])],
            request.Token));

        Assert.False(storage.CleanupObservedCancellation);
        Assert.Equal(17, Assert.Single(storage.DeletedGenerations));
    }

    [Fact]
    public async Task GetSignedUrlAsync_ObjectWithoutCleanMetadata_ReturnsNull()
    {
        var storage = new RecordingStorage();
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), new RecordingRepository());

        var result = await service.GetSignedUrlAsync("maliev.com", "uploads/missing.stl", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(storage.Signed);
    }

    [Fact]
    public async Task GetSignedUrlAsync_ReadOnlyStorage_AllowsExistingObjectReadsWhileWritesRemainDisabled()
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository();
        repository.Uploads.Add(new Upload
        {
            Bucket = "maliev.com",
            Name = "uploads/existing.stl",
            ContentType = "model/stl",
            Size = 3,
        });
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository, writesEnabled: false);

        storage.SeedLive("maliev.com", "uploads/existing.stl", 3);
        var result = await service.GetSignedUrlAsync("maliev.com", "uploads/existing.stl", CancellationToken.None);

        Assert.Equal(new Uri("https://storage.test/uploads/existing.stl"), result);
        Assert.Equal(("maliev.com", "uploads/existing.stl"), Assert.Single(storage.Signed));
        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("part.stl", "model/stl", [1])],
            CancellationToken.None));
    }

    [Fact]
    public async Task MetadataCommitResponseLoss_RetainsPromotedObjectForDeterministicReconciliation()
    {
        var storage = new RecordingStorage(); var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.Parse("5d034fac-25b1-4ba0-bfe2-502ab26471ca");
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            service.UploadAsync("maliev.com", "orders/42", files, operationId, default));
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Empty(storage.Deleted);
        repository.ThrowAfterAdd = false;
        var reconciled = await service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default);
        Assert.NotNull(reconciled); Assert.Single(reconciled.Object);
    }

    [Fact]
    public async Task ReconcileUploadAsync_SameSizeReplacementGeneration_DoesNotIssueSignedUrl()
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.NewGuid();
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            service.UploadAsync("maliev.com", "orders/42", files, operationId, default));
        storage.LiveGeneration = 32;

        Assert.Null(await service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default));
        Assert.Single(storage.Signed);
    }

    [Theory]
    [InlineData(32L)]
    [InlineData(999999L)]
    public async Task ReconcileUploadAsync_ReplacementAfterEvidenceRead_SignsOnlyAcknowledgedGeneration(long replacement)
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.NewGuid();
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync("maliev.com", "orders/42", files, operationId, default));
        storage.ReplacementAfterEvidence = replacement;

        var result = await service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default);

        Assert.NotNull(result);
        Assert.Single(result.Object);
        Assert.Equal(replacement, storage.LiveGeneration);
        var signed = Assert.Single(storage.GenerationSigned);
        Assert.Equal(31, signed.Generation);
        Assert.Equal("maliev.com", signed.Bucket);
        Assert.Equal(Assert.Single(repository.Uploads).Name, signed.ObjectName);
        Assert.Equal(1, storage.NameOnlySignCalls);
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task ReconcileUploadAsync_StableEvidence_UsesGenerationSigner()
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.NewGuid();
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync("maliev.com", "orders/42", files, operationId, default));

        Assert.NotNull(await service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default));
        Assert.Equal(31, Assert.Single(storage.GenerationSigned).Generation);
        Assert.Equal(1, storage.NameOnlySignCalls);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task ReconcileUploadAsync_NonpositiveJournalGeneration_RefusesBeforeReadingOrSigning(long generation)
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.NewGuid();
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync("maliev.com", "orders/42", files, operationId, default));
        var journal = Assert.IsType<RecordingMoveJournal>(storage.Journal);
        var entry = Assert.Single(journal.Evidence);
        journal.Evidence[entry.Key] = entry.Value with { DestinationGeneration = generation };
        storage.LiveGeneration = generation;

        Assert.Null(await service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default));
        Assert.Equal(0, storage.EvidenceReads);
        Assert.Empty(storage.GenerationSigned);
        Assert.Equal(1, storage.NameOnlySignCalls);
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task ReconcileUploadAsync_GenerationSignerFailure_PreservesCauseAndRecoveryObjects()
    {
        var signingFailure = new IOException("recovery signing unavailable");
        var storage = new RecordingStorage { SignedUrlFailure = signingFailure, SignedUrlFailureCall = 2 };
        var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.NewGuid();
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync("maliev.com", "orders/42", files, operationId, default));

        Assert.Same(signingFailure, await Assert.ThrowsAsync<IOException>(() => service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default)));
        Assert.Equal(31, Assert.Single(storage.GenerationSigned).Generation);
        Assert.Equal(1, storage.NameOnlySignCalls);
        Assert.Single(repository.Uploads);
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task MoveAsync_MetadataCommitResponseLost_ReportsUnknownWithJournalEvidence()
    {
        var storage = new RecordingStorage();
        var repository = new RecordingRepository { ThrowAfterMove = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        // Establish the required lineage through the normal successful upload path;
        // this is a unit prerequisite, not provider or malware certification.
        await service.UploadAsync("maliev.com", "orders",
            [new MemoryUploadFile("source.step", "application/step", [1, 2, 3])], default);
        var prior = Assert.Single(storage.Journal!.Evidence);
        Assert.Equal("MetadataCommitted", prior.Value.State);
        storage.Moved.Clear();

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            service.MoveAsync("maliev.com", "orders/source.step", "maliev.com", "orders/destination.step", default));

        Assert.IsType<IOException>(failure.InnerException);
        Assert.Single(storage.Moved);
        Assert.Empty(storage.Deleted);
        Assert.Contains(storage.Journal!.Evidence.Values, evidence => evidence.State == "Unknown"
            && evidence.DestinationGeneration == 31);
        Assert.Equal(prior.Value, storage.Journal.Evidence[prior.Key]);
    }

    [Fact]
    public async Task UploadAsync_ConfirmedSigningFailure_CompensatesExactPromotedGenerationsBeforeMetadata()
    {
        var expected = new InvalidOperationException("signing unavailable");
        var storage = new RecordingStorage
        {
            SignedUrlFailure = expected,
            SignedUrlFailureCall = 2,
        };
        var repository = new RecordingRepository();
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);

        var exception = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync(
            "maliev.com",
            "orders/42",
            [
                new MemoryUploadFile("first.step", "application/step", [1, 2, 3]),
                new MemoryUploadFile("second.step", "application/step", [4, 5, 6]),
            ],
            CancellationToken.None));

        Assert.Same(expected, exception.InnerException);
        Assert.Equal(2, storage.Moved.Count);
        Assert.Equal(new long[] { 31, 31 }, storage.DeletedGenerations);
        Assert.Equal(0, storage.NameOnlyDeleteCalls);
        Assert.Empty(repository.Uploads);
        Assert.Equal(0, repository.AddRangeCallCount);
    }

    [Fact]
    public async Task UploadAsync_ConfirmedSigningFailure_UsesIndependentBoundedCleanupOnCallerCancellation()
    {
        using var request = new CancellationTokenSource();
        var storage = new RecordingStorage
        {
            SignedUrlFailure = new InvalidOperationException("signing unavailable"),
            SignedUrlFailureCall = 1,
            RequestCancellation = request,
        };
        var service = CreateService(
            storage,
            new StubScanner(new(FileSafetyVerdict.Clean)),
            new RecordingRepository());

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("part.step", "application/step", [1, 2, 3])],
            request.Token));

        Assert.True(request.IsCancellationRequested);
        Assert.False(storage.CleanupObservedCancellation);
        Assert.Equal(new long[] { 31 }, storage.DeletedGenerations);
        Assert.Equal(0, storage.NameOnlyDeleteCalls);
    }

    [Fact]
    public async Task UploadAsync_SignedUrlFailure_DoesNotAttemptNameOnlyRollback()
    {
        var signingFailure = new InvalidOperationException("signing unavailable");
        var cleanupFailure = new IOException("storage delete unavailable");
        var storage = new RecordingStorage
        {
            SignedUrlFailure = signingFailure,
            SignedUrlFailureCall = 1,
        };
        storage.DeleteFailures["orders/42/part.step"] = cleanupFailure;
        var repository = new RecordingRepository();
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);

        var exception = await Assert.ThrowsAsync<UploadRollbackException>(() => service.UploadAsync(
            "maliev.com",
            "orders/42",
            [new MemoryUploadFile("part.step", "application/step", [1, 2, 3])],
            CancellationToken.None));

        Assert.Same(signingFailure, exception.UploadFailure);
        Assert.Same(cleanupFailure, Assert.Single(exception.CleanupFailures).Cause);
        Assert.Equal(new long[] { 31 }, storage.DeletedGenerations);
        Assert.Equal(0, storage.NameOnlyDeleteCalls);
        Assert.Empty(repository.Uploads);
        Assert.Equal(0, repository.AddRangeCallCount);
    }

    [Fact]
    public async Task UploadAsync_MultipleConfirmedPromotions_AttemptsEveryExactGenerationAndRetainsAllSigningCleanupCauses()
    {
        var signingFailure = new InvalidOperationException("signing unavailable");
        var firstCleanupFailure = new IOException("first delete unavailable");
        var thirdCleanupFailure = new TimeoutException("third delete timed out");
        var storage = new RecordingStorage
        {
            SignedUrlFailure = signingFailure,
            SignedUrlFailureCall = 3,
        };
        storage.DeleteFailures["orders/42/first.step"] = firstCleanupFailure;
        storage.DeleteFailures["orders/42/third.step"] = thirdCleanupFailure;
        var service = CreateService(
            storage,
            new StubScanner(new(FileSafetyVerdict.Clean)),
            new RecordingRepository());

        var exception = await Assert.ThrowsAsync<UploadRollbackException>(() => service.UploadAsync(
            "maliev.com",
            "orders/42",
            [
                new MemoryUploadFile("first.step", "application/step", [1]),
                new MemoryUploadFile("second.step", "application/step", [2]),
                new MemoryUploadFile("third.step", "application/step", [3]),
            ],
            CancellationToken.None));

        Assert.Same(signingFailure, exception.UploadFailure);
        Assert.Equal(2, exception.CleanupFailures.Count);
        Assert.Contains(exception.CleanupFailures, item => ReferenceEquals(item.Cause, firstCleanupFailure));
        Assert.Contains(exception.CleanupFailures, item => ReferenceEquals(item.Cause, thirdCleanupFailure));
        Assert.Equal(3, storage.Moved.Count);
        Assert.Equal(new long[] { 31, 31, 31 }, storage.DeletedGenerations);
        Assert.Equal(0, storage.NameOnlyDeleteCalls);
    }

    [Fact]
    public async Task UploadAsync_ConfirmedSigningFailure_AlreadyAbsentExactGenerationRetainsOriginalCause()
    {
        var signingFailure = new InvalidOperationException("signing unavailable");
        var storage = new RecordingStorage
        {
            DeleteResult = false,
            SignedUrlFailure = signingFailure,
            SignedUrlFailureCall = 1,
        };
        var service = CreateService(
            storage,
            new StubScanner(new(FileSafetyVerdict.Clean)),
            new RecordingRepository());

        var exception = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("part.step", "application/step", [1, 2, 3])],
            CancellationToken.None));

        Assert.Same(signingFailure, exception.InnerException);
        Assert.Equal(new long[] { 31 }, storage.DeletedGenerations);
        Assert.Equal(0, storage.NameOnlyDeleteCalls);
        Assert.Equal("CompensatedAbsent", Assert.Single(storage.Journal!.Evidence.Values).State);
    }

    [Fact]
    public void MultipartEnvelopeAllowance_PreservesExactAggregateFileLimit()
    {
        Assert.Equal(100L * 1024L * 1024L, FileApplicationService.MaximumUploadBytes);
        Assert.InRange(FileApplicationService.MaximumRequestBytes - FileApplicationService.MaximumUploadBytes, 1, 1024L * 1024L);
    }

    [Fact]
    public async Task UploadAsync_OverEdgeAggregateLimit_RejectsBeforeStorageAndScanning()
    {
        var storage = new RecordingStorage();
        var scanner = new StubScanner(new FileSafetyResult(FileSafetyVerdict.Clean));
        var repository = new RecordingRepository();
        var service = CreateService(storage, scanner, repository);

        var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() => service.UploadAsync(
            "maliev.com", null, [new ReportedLengthUploadFile(100L * 1024 * 1024 + 1)], CancellationToken.None));

        Assert.Equal("Total upload size cannot exceed 100 MB", exception.Message);
        Assert.Empty(storage.Uploaded);
        Assert.Empty(repository.Uploads);
    }

    private static FileApplicationService CreateService(
        RecordingStorage storage,
        IFileSafetyScanner scanner,
        RecordingRepository repository,
        bool writesEnabled = true)
    {
        var options = Options.Create(new FileStorageOptions
        {
            Enabled = true,
            WritesEnabled = writesEnabled,
            AllowedBuckets = ["maliev.com"],
            QuarantinePrefix = "_quarantine",
            SignedUrlHours = 168,
        });
        var time = new FakeTimeProvider(Now);
        var moveJournal = new RecordingMoveJournal();
        storage.Journal = moveJournal;
        return new FileApplicationService(
            storage,
            scanner,
            repository,
            moveJournal,
            new ObjectNamePolicy(options, time),
            options,
            new LegacyFileRuntimeGate(options),
            NullLogger<FileApplicationService>.Instance,
            new RecordingQuarantineUploadIntent(), new UploadSnapshotCapture(), moveJournal);
    }

    private sealed class MemoryUploadFile(string name, string contentType, byte[] bytes) : IUploadFile
    {
        public string FileName => name;
        public string ContentType => contentType;
        public long Length => bytes.LongLength;
        public Stream OpenReadStream() => new MemoryStream(bytes, writable: false);
    }

    private sealed class ReportedLengthUploadFile(long length) : IUploadFile
    {
        public string FileName => "part.step";
        public string ContentType => "application/step";
        public long Length => length;
        public Stream OpenReadStream() => throw new InvalidOperationException("Oversized input must not be opened.");
    }

    private sealed class StubScanner(FileSafetyResult result, Action? beforeResult = null) : IFileSafetyScanner
    {
        public Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken cancellationToken)
        {
            beforeResult?.Invoke();
            return Task.FromResult(result);
        }
    }

    // Unit fixture only; production requires PostgreSQL authority with physical schema admission.
    private sealed class RecordingQuarantineUploadIntent : IQuarantineUploadIntent
    {
        public Task PrepareAsync(Guid operationId, Guid parentOperationId, string bucket, string objectName,
            string contentType, long declaredSize, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcknowledgeAsync(Guid operationId, long generation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UnknownAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingMoveJournal : IStorageMoveJournal, IStorageReadJournal
    {
        public Task<bool> TryBeginMetadataSubmissionAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken token) =>
            Task.FromResult(Transition(claims, "SourceDeleted", "MetadataSubmitting", token));
        public Task<bool> TryBeginCompensationAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken token) =>
            Task.FromResult(Transition(claims, "SourceDeleted", "CompensationPending", token));
        public Task MetadataSubmissionCommittedAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken token)
        {
            if (!Transition(claims, "MetadataSubmitting", "MetadataCommitted", token)) throw new UploadOutcomeUnknownException("fixture checkpoint changed");
            return Task.CompletedTask;
        }
        public Task RecordCompensationAsync(StorageMoveClaim claim, CompensationDisposition disposition, CancellationToken token)
        {
            var state = disposition switch
            {
                CompensationDisposition.Removed => "CompensatedRemoved",
                CompensationDisposition.Absent => "CompensatedAbsent",
                _ => "CompensationUnknown",
            };
            if (!Transition([claim], "CompensationPending", state, token)) throw new UploadOutcomeUnknownException("fixture checkpoint changed");
            return Task.CompletedTask;
        }
        public Task<StorageMoveEvidence?> FindCommittedSourceAsync(string bucket, string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var matches = Evidence.Values.Where(item => item.DestinationBucket == bucket && item.DestinationObjectName == name).ToArray();
            return Task.FromResult(matches.Length == 1 && matches[0].ScanClean && matches[0].State == "MetadataCommitted"
                && matches[0].SourceGeneration > 0 && matches[0].DestinationGeneration > 0 ? matches[0] : null);
        }
        public async Task<StorageReadEvidence> FindReadEvidenceAsync(string bucket, string name, CancellationToken token)
        {
            var evidence = await FindCommittedSourceAsync(bucket, name, token);
            return evidence is null ? new(StorageReadState.Absent) : new(StorageReadState.Confirmed, evidence);
        }
        private bool Transition(IReadOnlyList<StorageMoveClaim> claims, string expected, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (claims.Count == 0 || claims.Select(item => item.OperationId).Distinct().Count() != claims.Count
                || claims.Any(item => !Evidence.TryGetValue(item.OperationId, out var current) || current.State != expected
                    || current with { State = "SourceDeleted" } != item.Evidence)) return false;
            foreach (var claim in claims) Evidence[claim.OperationId] = Evidence[claim.OperationId] with { State = target };
            return true;
        }
        public Dictionary<Guid, StorageMoveEvidence> Evidence { get; } = [];
        public Task<StorageMoveEvidence?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Evidence.GetValueOrDefault(operationId));
        public Task<bool> BeginAsync(Guid operationId, bool scanClean, string sourceBucket, string sourceObjectName,
            long sourceGeneration, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task CopiedAsync(Guid operationId, long destinationGeneration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SourceDeletedAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MetadataCommittedAsync(Guid operationId, CancellationToken cancellationToken)
        {
            if (Evidence.TryGetValue(operationId, out var evidence)) Evidence[operationId] = evidence with { State = "MetadataCommitted" };
            return Task.CompletedTask;
        }
        public Task UnknownAsync(Guid operationId, CancellationToken cancellationToken)
        {
            if (Evidence.TryGetValue(operationId, out var evidence) && evidence.State is "SourceObserved" or "Copied" or "SourceDeleted" or "Unknown")
                Evidence[operationId] = evidence with { State = "Unknown" };
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStorage : IObjectStorage
    {
        public RecordingMoveJournal? Journal { get; set; }
        public long LiveGeneration { get; set; } = 31;
        public long? ReplacementAfterEvidence { get; set; }
        public int EvidenceReads { get; private set; }
        public int NameOnlySignCalls { get; private set; }
        public List<(string Bucket, string ObjectName, long Generation)> GenerationSigned { get; } = [];
        public List<(string Bucket, string ObjectName)> Uploaded { get; } = [];
        public List<(string SourceObjectName, string DestinationObjectName)> Moved { get; } = [];
        public List<(string Bucket, string ObjectName)> Deleted { get; } = [];
        public List<long> DeletedGenerations { get; } = [];
        public int NameOnlyDeleteCalls { get; private set; }
        public List<(string Bucket, string ObjectName)> Signed { get; } = [];
        private readonly Dictionary<(string Bucket, string ObjectName), long> sizes = [];
        public bool CleanupObservedCancellation { get; private set; }
        public Exception? SignedUrlFailure { get; init; }
        public int SignedUrlFailureCall { get; init; }
        public CancellationTokenSource? RequestCancellation { get; init; }
        public bool DeleteResult { get; init; } = true;
        public Exception? MoveFailure { get; init; }
        public bool MoveResult { get; init; } = true;
        public Dictionary<string, Exception> DeleteFailures { get; } = [];

        public Task UploadAsync(string bucket, string objectName, string contentType, Stream content, CancellationToken cancellationToken)
        {
            Uploaded.Add((bucket, objectName));
            sizes[(bucket, objectName)] = content.Length;
            return Task.CompletedTask;
        }

        public async Task<long> UploadGenerationAsync(string bucket, string objectName, string contentType,
            Stream content, CancellationToken cancellationToken)
        {
            await UploadAsync(bucket, objectName, contentType, content, cancellationToken);
            return 17;
        }

        public Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
        {
            Moved.Add((sourceObjectName, destinationObjectName));
            if (MoveFailure is not null) return Task.FromException<bool>(MoveFailure);
            if (sizes.Remove((sourceBucket, sourceObjectName), out var size)) sizes[(destinationBucket, destinationObjectName)] = size;
            return Task.FromResult(true);
        }

        public async Task<bool> MoveJournaledAsync(Guid operationId, long? expectedSourceGeneration, bool scanClean, string sourceBucket, string sourceObjectName,
            string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
        {
            if (!MoveResult) return false;
            var moved = await MoveAsync(sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken);
            if (moved && Journal is not null)
                Journal.Evidence[operationId] = new StorageMoveEvidence(scanClean, sourceBucket, sourceObjectName,
                    expectedSourceGeneration ?? 17, destinationBucket, destinationObjectName, 31, "SourceDeleted");
            return moved;
        }

        public Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
        {
            NameOnlyDeleteCalls++;
            return DeleteCore(bucket, objectName, cancellationToken);
        }

        private Task<bool> DeleteCore(string bucket, string objectName, CancellationToken cancellationToken)
        {
            CleanupObservedCancellation |= cancellationToken.IsCancellationRequested;
            Deleted.Add((bucket, objectName));
            if (DeleteFailures.TryGetValue(objectName, out var failure))
            {
                return Task.FromException<bool>(failure);
            }

            sizes.Remove((bucket, objectName));
            return Task.FromResult(DeleteResult);
        }

        public Task<bool> DeleteGenerationAsync(string bucket, string objectName, long generation, CancellationToken cancellationToken)
        {
            DeletedGenerations.Add(generation);
            return DeleteCore(bucket, objectName, cancellationToken);
        }

        public Task<long?> GetSizeAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
            Task.FromResult(sizes.TryGetValue((bucket, objectName), out var size) ? (long?)size : null);

        public void SeedLive(string bucket, string name, long size) => sizes[(bucket, name)] = size;

        public Task<StorageObjectEvidence?> GetEvidenceAsync(string bucket, string objectName, CancellationToken cancellationToken)
        {
            EvidenceReads++;
            StorageObjectEvidence? evidence = sizes.TryGetValue((bucket, objectName), out var size)
                ? new StorageObjectEvidence(LiveGeneration, size) : null;
            if (ReplacementAfterEvidence is long replacement) LiveGeneration = replacement;
            return Task.FromResult(evidence);
        }

        public Task<Uri> CreateSignedGenerationReadUriAsync(string bucket, string objectName, long generation, TimeSpan duration, CancellationToken token)
        {
            GenerationSigned.Add((bucket, objectName, generation));
            return SignCore(bucket, objectName);
        }

        public Task<Uri> CreateSignedReadUriAsync(string bucket, string objectName, TimeSpan duration, CancellationToken cancellationToken)
        {
            NameOnlySignCalls++;
            return SignCore(bucket, objectName);
        }

        private Task<Uri> SignCore(string bucket, string objectName)
        {
            Signed.Add((bucket, objectName));
            if (SignedUrlFailure is not null && Signed.Count == SignedUrlFailureCall)
            {
                RequestCancellation?.Cancel();
                return Task.FromException<Uri>(SignedUrlFailure);
            }

            return Task.FromResult(new Uri($"https://storage.test/{objectName}"));
        }
    }

    private sealed class RecordingRepository : IUploadRepository
    {
        public List<Upload> Uploads { get; } = [];
        public bool ThrowAfterAdd { get; set; }
        public bool ThrowAfterMove { get; set; }
        public int AddRangeCallCount { get; private set; }

        public Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken cancellationToken)
        {
            AddRangeCallCount++;
            Uploads.AddRange(uploads);
            if (ThrowAfterAdd) throw new IOException("metadata response lost");
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
            Task.FromResult(Uploads.Any(upload => upload.Bucket == bucket && upload.Name == objectName));

        public Task DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
        {
            Uploads.RemoveAll(upload => upload.Bucket == bucket && upload.Name == objectName);
            return Task.CompletedTask;
        }

        public Task MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
        {
            var upload = Uploads.Single(item => item.Bucket == sourceBucket && item.Name == sourceObjectName);
            upload.Bucket = destinationBucket;
            upload.Name = destinationObjectName;
            if (ThrowAfterMove) throw new IOException("metadata response lost");
            return Task.CompletedTask;
        }
    }
}
