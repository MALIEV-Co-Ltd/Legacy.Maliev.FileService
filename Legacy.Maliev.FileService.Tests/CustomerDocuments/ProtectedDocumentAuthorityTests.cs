using System.Security.Claims;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.Extensions.Options;
using Moq;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class ProtectedDocumentAuthorityTests
{
    [Fact]
    public async Task UnknownActorKindFailsClosedBeforeRegistryRead()
    {
        var authority = new Mock<ICustomerDocumentAuthority>();
        authority.Setup(x => x.AuthorizeAsync(It.IsAny<DocumentActor>(), 23, CustomerDocumentPermissions.Read, It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Allowed, (DocumentActorKind)99, "synthetic"));
        var registry = new Mock<ICustomerDocumentRegistry>();
        registry.Setup(x => x.ReadReceiptAsync(It.IsAny<DocumentActor>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Registry must not be reached by malformed authority"));
        var service = new CustomerDocumentDownloadService(authority.Object, new Mock<IProtectedDocumentStore>().Object, new Mock<IProtectedDocumentStorage>().Object, registry.Object, new Mock<IDocumentProtectionService>().Object, Options.Create(new CustomerDocumentOptions { Enabled = true }));
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => service.ReadAsync(new(new ClaimsPrincipal()), 23, Guid.NewGuid(), Guid.NewGuid(), default));
        registry.Verify(x => x.ReadReceiptAsync(It.IsAny<DocumentActor>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task WrongCustomerAuthorityCannotReadValidDocumentIdentity()
    {
        var authority = new Mock<ICustomerDocumentAuthority>();
        authority.Setup(x => x.AuthorizeAsync(It.IsAny<DocumentActor>(), 24, CustomerDocumentPermissions.Read, It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Denied));
        var service = new CustomerDocumentDownloadService(authority.Object, new Mock<IProtectedDocumentStore>().Object, new Mock<IProtectedDocumentStorage>().Object, new Mock<ICustomerDocumentRegistry>().Object, new Mock<IDocumentProtectionService>().Object, Options.Create(new CustomerDocumentOptions { Enabled = true }));
        await Assert.ThrowsAsync<DocumentAuthorityDeniedException>(() => service.ReadAsync(new(new ClaimsPrincipal()), 24, Guid.NewGuid(), Guid.NewGuid(), default));
    }
}
