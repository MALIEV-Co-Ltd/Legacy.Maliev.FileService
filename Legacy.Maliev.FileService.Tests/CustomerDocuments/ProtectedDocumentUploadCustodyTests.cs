using System.Security.Cryptography;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.Extensions.Options;
using Moq;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class ProtectedDocumentUploadCustodyTests
{
    [Fact]
    public async Task ScannerUnavailableRetainsAcknowledgedPrivateGeneration()
    {
        var objects = new Mock<IObjectStorage>();
        objects.Setup(x => x.UploadGenerationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(71);
        var scanner = new Mock<IFileSafetyScanner>();
        scanner.Setup(x => x.ScanAsync(It.IsAny<IUploadFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new FileSafetyResult(FileSafetyVerdict.Unavailable));
        var custody = new Custody();
        var reader = new Mock<IProtectedDocumentGenerationReader>(); reader.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), 71, It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(Content().Bytes);
        var storage = new CustomerDocumentStorageAdapter(objects.Object, scanner.Object, new Mock<IStorageMoveJournal>().Object, reader.Object, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic" }), custody);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => storage.StoreAsync(Reservation(), Content(), default));
        Assert.Equal(71, custody.Generation);
        Assert.StartsWith("customer-documents/23/", custody.ObjectName);
    }
    [Fact]
    public async Task LostInitialUploadResponseRetainsPrivateCoordinatesBeforeProvider()
    {
        var objects = new Mock<IObjectStorage>();
        objects.Setup(x => x.UploadGenerationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Synthetic lost response"));
        var custody = new Custody();
        var storage = new CustomerDocumentStorageAdapter(objects.Object, new Mock<IFileSafetyScanner>().Object, new Mock<IStorageMoveJournal>().Object, new UnavailableProtectedDocumentGenerationReader(), Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic" }), custody);
        Assert.NotNull(await Record.ExceptionAsync(() => storage.StoreAsync(Reservation(), Content(), default)));
        Assert.StartsWith("customer-documents/23/", custody.ObjectName);
        Assert.Null(custody.Generation);
    }
    [Fact]
    public async Task SameSizeSubstitutedQuarantineBytesCannotReceiveCleanPromotion()
    {
        var reservation = Reservation(); var content = Content();
        var prefix = $"customer-documents/23/{reservation.DocumentId:N}/{reservation.VersionId:N}";
        var source = $"{prefix}/quarantine/{reservation.OperationId:N}";
        var destination = $"{prefix}/original";
        var objects = new Mock<IObjectStorage>();
        objects.Setup(x => x.UploadGenerationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(71);
        objects.Setup(x => x.GetEvidenceAsync("synthetic", source, It.IsAny<CancellationToken>())).ReturnsAsync(new StorageObjectEvidence(71, content.Bytes.Length));
        objects.Setup(x => x.MoveJournaledAsync(reservation.OperationId, 71, true, "synthetic", source, "synthetic", destination, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var scanner = new Mock<IFileSafetyScanner>(); scanner.Setup(x => x.ScanAsync(It.IsAny<IUploadFile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new FileSafetyResult(FileSafetyVerdict.Clean));
        var journal = new Mock<IStorageMoveJournal>(); journal.SetupSequence(x => x.FindAsync(reservation.OperationId, It.IsAny<CancellationToken>())).ReturnsAsync((StorageMoveEvidence?)null).ReturnsAsync(new StorageMoveEvidence(true, "synthetic", source, 71, "synthetic", destination, 72, "SourceDeleted"));
        var replaced = content.Bytes.ToArray(); replaced[10] ^= 1;
        var reader = new Mock<IProtectedDocumentGenerationReader>(); reader.Setup(x => x.ReadAsync("synthetic", source, 71, It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReadOnlyMemory<byte>(replaced));
        var storage = new CustomerDocumentStorageAdapter(objects.Object, scanner.Object, journal.Object, reader.Object, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic" }), new Custody());
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => storage.StoreAsync(reservation, content, default));
    }
    private static DocumentUploadReservation Reservation() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 23, 1, new string('b', 64));
    private static ValidatedDocumentContent Content()
    {
        byte[] bytes = "%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF"u8.ToArray();
        return new(bytes, "application/pdf", Convert.ToHexStringLower(SHA256.HashData(bytes)), "synthetic.pdf");
    }
    private sealed class Custody : IQuarantineUploadIntent
    {
        public string ObjectName { get; private set; } = "";
        public long? Generation { get; private set; }
        public Task PrepareAsync(Guid operationId, Guid parentOperationId, string bucket, string objectName, string contentType, long declaredSize, CancellationToken token) { ObjectName = objectName; return Task.CompletedTask; }
        public Task AcknowledgeAsync(Guid operationId, long generation, CancellationToken token) { Generation = generation; return Task.CompletedTask; }
        public Task UnknownAsync(Guid operationId, CancellationToken token) => Task.CompletedTask;
    }
}
