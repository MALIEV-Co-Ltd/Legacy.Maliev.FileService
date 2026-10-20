using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Pending genuine RED against the unpatched legacy service, then GREEN against the
// exact owner-integrated prefix guard. No provider, database or scanner is contacted.
public sealed class ProtectedDocumentGenericUploadGuardTests
{
    [Theory]
    [InlineData(UploadAcquireState.Replay, true)]
    [InlineData(UploadAcquireState.Replay, false)]
    [InlineData(UploadAcquireState.Unknown, true)]
    [InlineData(UploadAcquireState.Unknown, false)]
    public async Task NonreservedRequest_RefusesReservedStoredPathOrObjectAfterAcquisition(UploadAcquireState state, bool reservedEffectivePath)
    {
        var cached = new UploadResultResponse([new("synthetic",
            reservedEffectivePath ? "uploads/order/23/original.pdf" : " /CUSTOMER-DOCUMENTS\\23//original.pdf ",
            new Uri("https://signed.example.invalid/cached"))]);
        var store = CachedStore(state, cached, reservedEffectivePath ? " /CUSTOMER-DOCUMENTS\\23/ " : "uploads/order/23");
        var coordinator = new IdempotentUploadCoordinator(store.Object, new UploadSnapshotCapture());
        await Assert.ThrowsAsync<FileUploadValidationException>(() => coordinator.ExecuteAsync(
            "synthetic-staff", "synthetic-key", "synthetic", "uploads/request/23", [new SyntheticFile()],
            (_, _, _, _) => throw new InvalidOperationException("Stored-response test must not execute storage."),
            (_, _, _, _) => throw new InvalidOperationException("Stored-response test must not reconcile storage."), default));
        VerifyAcquiredWithoutCompletion(store);
    }

    [Theory]
    [InlineData(UploadAcquireState.Replay)]
    [InlineData(UploadAcquireState.Unknown)]
    public async Task MissingCachedObjectName_IsUnavailableBeforeReturnOrCompletion(UploadAcquireState state)
    {
        var cached = new UploadResultResponse([new("synthetic", null!, new Uri("https://signed.example.invalid/cached"))]);
        var store = CachedStore(state, cached, "uploads/order/23");
        var coordinator = new IdempotentUploadCoordinator(store.Object, new UploadSnapshotCapture());
        await Assert.ThrowsAsync<UploadIdempotencyUnavailableException>(() => coordinator.ExecuteAsync(
            "synthetic-staff", "synthetic-key", "synthetic", "uploads/request/23", [new SyntheticFile()],
            (_, _, _, _) => throw new InvalidOperationException("Invalid cache must not execute storage."),
            (_, _, _, _) => throw new InvalidOperationException("Invalid cache must not reconcile storage."), default));
        VerifyAcquiredWithoutCompletion(store);
    }

    [Theory]
    [InlineData(UploadAcquireState.Replay)]
    [InlineData(UploadAcquireState.Unknown)]
    public async Task CachedReservedResponse_CannotBypassUploadAndReconcileGuards(UploadAcquireState state)
    {
        var cached = new UploadResultResponse([new("synthetic", "customer-documents/23/original.pdf",
            new Uri("https://signed.example.invalid/reserved"))]);
        var store = new Mock<IUploadIdempotencyStore>(MockBehavior.Strict);
        store.Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadAcquireResult(state, "synthetic-reservation", cached, "customer-documents/23"));
        store.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), cached, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var coordinator = new IdempotentUploadCoordinator(store.Object, new UploadSnapshotCapture());
        await Assert.ThrowsAsync<FileUploadValidationException>(() => coordinator.ExecuteAsync(
            "synthetic-staff", "synthetic-key", "synthetic", "customer-documents/23", [new SyntheticFile()],
            (_, _, _, _) => throw new InvalidOperationException("Cached-response test must not execute storage."),
            (_, _, _, _) => throw new InvalidOperationException("Cached-response test must not reconcile storage."), default));
        store.Verify(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<UploadResultResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NonreservedCachedReplay_PreservesItsExistingResponse()
    {
        var cached = new UploadResultResponse([new("synthetic", "uploads/order/23/original.pdf",
            new Uri("https://signed.example.invalid/ordinary"))]);
        var store = new Mock<IUploadIdempotencyStore>(MockBehavior.Strict);
        store.Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadAcquireResult(UploadAcquireState.Replay, "synthetic-reservation", cached, "uploads/order/23"));
        var coordinator = new IdempotentUploadCoordinator(store.Object, new UploadSnapshotCapture());
        var result = await coordinator.ExecuteAsync("synthetic-staff", "synthetic-key", "synthetic", "uploads/order/23", [new SyntheticFile()],
            (_, _, _, _) => throw new InvalidOperationException("Replay must not execute storage."),
            (_, _, _, _) => throw new InvalidOperationException("Replay must not reconcile storage."), default);
        Assert.Same(cached, result);
    }

    [Theory]
    [InlineData("customer-documents")]
    [InlineData("/CUSTOMER-DOCUMENTS//23")]
    [InlineData("\\customer-documents\\23")]
    [InlineData(" customer-documents/23 ")]
    public async Task Upload_RefusesNormalizedReservedPathBeforeDurableIntent(string path)
    {
        var fixture = Create();
        await Assert.ThrowsAsync<FileUploadValidationException>(() => fixture.Service.UploadAsync(
            "synthetic", path, [new SyntheticFile()], Guid.NewGuid(), default));
        fixture.AssertNoBoundaryCalls();
    }

    [Theory]
    [InlineData("customer-documents")]
    [InlineData("/CUSTOMER-DOCUMENTS//23")]
    [InlineData("\\customer-documents\\23")]
    [InlineData(" customer-documents/23 ")]
    public async Task Reconcile_RefusesNormalizedReservedPathBeforeJournalOrSigning(string path)
    {
        var fixture = Create();
        await Assert.ThrowsAsync<FileUploadValidationException>(() => fixture.Service.ReconcileUploadAsync(
            "synthetic", path, [new SyntheticFile()], Guid.NewGuid(), default));
        fixture.AssertNoBoundaryCalls();
    }

    [Fact]
    public async Task NonreservedUpload_StillReachesItsDurableIntentBoundary()
    {
        var fixture = Create();
        var error = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => fixture.Service.UploadAsync(
            "synthetic", "uploads/customer-documents", [new SyntheticFile()], Guid.NewGuid(), default));
        Assert.Same(fixture.Sentinel, error.InnerException);
        Assert.Single(fixture.Intent.Invocations);
    }

    [Fact]
    public async Task SimilarPrefixReconcile_StillReachesItsJournalBoundary()
    {
        var fixture = Create();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReconcileUploadAsync(
            "synthetic", "customer-documents-other/23", [new SyntheticFile()], Guid.NewGuid(), default));
        Assert.Same(fixture.Sentinel, error);
        Assert.Single(fixture.Journal.Invocations);
    }

    private static Boundaries Create()
    {
        var boundaries = new Boundaries();
        boundaries.Intent.Setup(x => x.PrepareAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ThrowsAsync(boundaries.Sentinel);
        boundaries.Journal.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(boundaries.Sentinel);
        var options = Options.Create(new FileStorageOptions
        {
            Enabled = true,
            WritesEnabled = true,
            AllowedBuckets = ["synthetic"],
            QuarantinePrefix = "_quarantine",
            SignedUrlHours = 1
        });
        boundaries.Service = new FileApplicationService(boundaries.Objects.Object, boundaries.Scanner.Object,
            boundaries.Repository.Object, boundaries.Journal.Object, new ObjectNamePolicy(options, TimeProvider.System),
            options, new LegacyFileRuntimeGate(options), NullLogger<FileApplicationService>.Instance,
            boundaries.Intent.Object, new UploadSnapshotCapture(), boundaries.ReadJournal.Object);
        return boundaries;
    }

    private static Mock<IUploadIdempotencyStore> CachedStore(UploadAcquireState state, UploadResultResponse response, string effectivePath)
    {
        var store = new Mock<IUploadIdempotencyStore>(MockBehavior.Strict);
        store.Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadAcquireResult(state, "synthetic-reservation", response, effectivePath));
        store.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), response, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return store;
    }

    private static void VerifyAcquiredWithoutCompletion(Mock<IUploadIdempotencyStore> store)
    {
        store.Verify(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<UploadResultResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Boundaries
    {
        public FileApplicationService Service { get; set; } = null!;
        public InvalidOperationException Sentinel { get; } = new("Synthetic forbidden-boundary observation.");
        public Mock<IObjectStorage> Objects { get; } = new(MockBehavior.Strict);
        public Mock<IFileSafetyScanner> Scanner { get; } = new(MockBehavior.Strict);
        public Mock<IUploadRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IStorageMoveJournal> Journal { get; } = new(MockBehavior.Strict);
        public Mock<IQuarantineUploadIntent> Intent { get; } = new(MockBehavior.Strict);
        public Mock<IStorageReadJournal> ReadJournal { get; } = new(MockBehavior.Strict);
        public void AssertNoBoundaryCalls()
        {
            Assert.Empty(Objects.Invocations);
            Assert.Empty(Scanner.Invocations);
            Assert.Empty(Repository.Invocations);
            Assert.Empty(Journal.Invocations);
            Assert.Empty(Intent.Invocations);
            Assert.Empty(ReadJournal.Invocations);
        }
    }

    private sealed class SyntheticFile : IUploadFile
    {
        public string FileName => "original.pdf";
        public string ContentType => "application/pdf";
        public long Length => 3;
        public Stream OpenReadStream() => new MemoryStream([1, 2, 3], false);
    }
}
