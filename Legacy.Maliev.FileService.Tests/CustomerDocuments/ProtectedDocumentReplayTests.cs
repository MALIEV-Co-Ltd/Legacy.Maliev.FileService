using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.Extensions.Options;
using Moq;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class ProtectedDocumentReplayTests
{
    [Fact]
    public async Task CompletedReplayCannotReturnReceiptAfterCleanEvidenceRevoked()
    {
        var actor = new DocumentActor(new ClaimsPrincipal());
        var file = new File();
        var captured = await DocumentContentValidator.ReadAsync(file, default);
        var receipt = new DocumentVersionReceipt(Guid.NewGuid(), Guid.NewGuid(), 23, 1, captured.Sha256, 2);
        var authority = new Mock<ICustomerDocumentAuthority>(); authority.Setup(x => x.AuthorizeAsync(actor, 23, CustomerDocumentPermissions.Write, It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, DocumentActorKind.Employee, "synthetic"));
        var associations = new Mock<ICustomerDocumentAssociationValidator>(); associations.Setup(x => x.ValidateAsync(actor, 23, It.IsAny<IReadOnlyList<DocumentAssociation>>(), It.IsAny<CancellationToken>())).ReturnsAsync(DocumentAuthorityOutcome.Allowed);
        var store = new Mock<IProtectedDocumentStore>(); store.Setup(x => x.ReserveAsync(23, "synthetic", "synthetic-key", It.IsAny<string>(), It.IsAny<DocumentUploadRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentUploadReservation(Guid.NewGuid(), receipt.DocumentId, receipt.VersionId, 23, 1, new string('b', 64), receipt));
        store.Setup(x => x.FindAsync(23, receipt.DocumentId, receipt.VersionId, DocumentActorKind.Employee, It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentStoredContent("synthetic", "customer-documents/23/original", 71, 70, Guid.NewGuid(), 30, receipt.ContentSha256, "application/pdf", "synthetic.pdf"));
        var storage = new Mock<IProtectedDocumentStorage>(); storage.Setup(x => x.ReadAsync(It.IsAny<DocumentStoredContent>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DocumentAuthorityUnavailableException());
        var service = new CustomerDocumentUploadService(authority.Object, associations.Object, store.Object, storage.Object, Options.Create(new CustomerDocumentOptions { Enabled = true }));
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => service.UploadAsync(actor, 23, new(null, DocumentKind.Nda, "Synthetic", DocumentVisibility.Customer, []), file, "synthetic-key", default));
        storage.Verify(x => x.ReadAsync(It.IsAny<DocumentStoredContent>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    private sealed class File : IUploadFile
    {
        private readonly byte[] bytes = "%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF"u8.ToArray();
        public string FileName => "synthetic.pdf";
        public string ContentType => "application/pdf";
        public long Length => bytes.Length;
        public Stream OpenReadStream() => new MemoryStream(bytes, false);
    }
}
