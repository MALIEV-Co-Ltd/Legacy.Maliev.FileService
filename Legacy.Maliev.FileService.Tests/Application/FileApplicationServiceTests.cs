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
    public async Task GetSignedUrlAsync_ObjectWithoutCleanMetadata_ReturnsNull()
    {
        var storage = new RecordingStorage();
        var service = CreateService(storage, new StubScanner(new(FileSafetyVerdict.Clean)), new RecordingRepository());

        var result = await service.GetSignedUrlAsync("maliev.com", "uploads/missing.stl", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(storage.Signed);
    }

    private static FileApplicationService CreateService(
        RecordingStorage storage,
        IFileSafetyScanner scanner,
        RecordingRepository repository)
    {
        var options = Options.Create(new FileStorageOptions
        {
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

        public Task UploadAsync(string bucket, string objectName, string contentType, Stream content, CancellationToken cancellationToken)
        {
            Uploaded.Add((bucket, objectName));
            return Task.CompletedTask;
        }

        public Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
        {
            Moved.Add((sourceObjectName, destinationObjectName));
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
        {
            Deleted.Add((bucket, objectName));
            return Task.FromResult(true);
        }

        public Task<Uri> CreateSignedReadUriAsync(string bucket, string objectName, TimeSpan duration, CancellationToken cancellationToken)
        {
            Signed.Add((bucket, objectName));
            return Task.FromResult(new Uri($"https://storage.test/{objectName}"));
        }
    }

    private sealed class RecordingRepository : IUploadRepository
    {
        public List<Upload> Uploads { get; } = [];

        public Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken cancellationToken)
        {
            Uploads.AddRange(uploads);
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
