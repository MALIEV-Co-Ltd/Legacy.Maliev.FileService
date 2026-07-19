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
    }

    [Fact]
    public async Task UploadAsync_KnownFailureUsesIndependentCleanupToken()
    {
        var storage = new RecordingStorage();
        var service = CreateService(
            storage,
            new StubScanner(new FileSafetyResult(FileSafetyVerdict.Unavailable)),
            new RecordingRepository());
        using var request = new CancellationTokenSource();
        request.Cancel();

        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => service.UploadAsync(
            "maliev.com",
            null,
            [new MemoryUploadFile("part.step", "application/step", [1])],
            request.Token));

        Assert.False(storage.CleanupObservedCancellation);
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
    public async Task MetadataCommitResponseLoss_RetainsPromotedObjectForDeterministicReconciliation()
    {
        var storage = new RecordingStorage(); var repository = new RecordingRepository { ThrowAfterAdd = true };
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), repository);
        var operationId = Guid.Parse("5d034fac-25b1-4ba0-bfe2-502ab26471ca");
        var files = new IUploadFile[] { new MemoryUploadFile("part.step", "application/step", [1, 2, 3]) };
        await Assert.ThrowsAsync<IOException>(() => service.UploadAsync("maliev.com", "orders/42", files, operationId, default));
        Assert.Empty(storage.Deleted);
        repository.ThrowAfterAdd = false;
        var reconciled = await service.ReconcileUploadAsync("maliev.com", "orders/42", files, operationId, default);
        Assert.NotNull(reconciled); Assert.Single(reconciled.Object);
    }

    [Fact]
    public void MultipartEnvelopeAllowance_PreservesExactAggregateFileLimit()
    {
        Assert.Equal(200L * 1024L * 1024L, FileApplicationService.MaximumUploadBytes);
        Assert.InRange(FileApplicationService.MaximumRequestBytes - FileApplicationService.MaximumUploadBytes, 1, 1024L * 1024L);
    }

    private static FileApplicationService CreateService(
        RecordingStorage storage,
        IFileSafetyScanner scanner,
        RecordingRepository repository)
    {
        var options = Options.Create(new FileStorageOptions
        {
            Enabled = true,
            WritesEnabled = true,
            AllowedBuckets = ["maliev.com"],
            QuarantinePrefix = "_quarantine",
            SignedUrlHours = 168,
        });
        var time = new FakeTimeProvider(Now);
        return new FileApplicationService(
            storage,
            scanner,
            repository,
            new ObjectNamePolicy(options, time),
            options,
            new LegacyFileRuntimeGate(options),
            NullLogger<FileApplicationService>.Instance);
    }

    private sealed class MemoryUploadFile(string name, string contentType, byte[] bytes) : IUploadFile
    {
        public string FileName => name;
        public string ContentType => contentType;
        public long Length => bytes.LongLength;
        public Stream OpenReadStream() => new MemoryStream(bytes, writable: false);
    }

    private sealed class StubScanner(FileSafetyResult result) : IFileSafetyScanner
    {
        public Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingStorage : IObjectStorage
    {
        public List<(string Bucket, string ObjectName)> Uploaded { get; } = [];
        public List<(string SourceObjectName, string DestinationObjectName)> Moved { get; } = [];
        public List<(string Bucket, string ObjectName)> Deleted { get; } = [];
        public List<(string Bucket, string ObjectName)> Signed { get; } = [];
        private readonly Dictionary<(string Bucket, string ObjectName), long> sizes = [];
        public bool CleanupObservedCancellation { get; private set; }

        public Task UploadAsync(string bucket, string objectName, string contentType, Stream content, CancellationToken cancellationToken)
        {
            Uploaded.Add((bucket, objectName));
            sizes[(bucket, objectName)] = content.Length;
            return Task.CompletedTask;
        }

        public Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
        {
            Moved.Add((sourceObjectName, destinationObjectName));
            if (sizes.Remove((sourceBucket, sourceObjectName), out var size)) sizes[(destinationBucket, destinationObjectName)] = size;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
        {
            CleanupObservedCancellation |= cancellationToken.IsCancellationRequested;
            Deleted.Add((bucket, objectName));
            sizes.Remove((bucket, objectName));
            return Task.FromResult(true);
        }

        public Task<long?> GetSizeAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
            Task.FromResult(sizes.TryGetValue((bucket, objectName), out var size) ? (long?)size : null);

        public Task<Uri> CreateSignedReadUriAsync(string bucket, string objectName, TimeSpan duration, CancellationToken cancellationToken)
        {
            Signed.Add((bucket, objectName));
            return Task.FromResult(new Uri($"https://storage.test/{objectName}"));
        }
    }

    private sealed class RecordingRepository : IUploadRepository
    {
        public List<Upload> Uploads { get; } = [];
        public bool ThrowAfterAdd { get; set; }

        public Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken cancellationToken)
        {
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
            return Task.CompletedTask;
        }
    }
}
