using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class FileDeleteLiteralIdentityTests
{
    [Fact]
    public Task Delete_LeadingSpace_PreservesLiteralIdentity() => AssertLiteralAsync(" leading.txt");

    [Fact]
    public Task Delete_TrailingSpace_PreservesLiteralIdentity() => AssertLiteralAsync("trailing.txt ");

    [Fact]
    public Task Delete_Case_PreservesLiteralIdentity() => AssertLiteralAsync("Case.TXT");

    [Fact]
    public Task Delete_ComposedUnicode_PreservesLiteralIdentity() => AssertLiteralAsync("caf\u00e9.txt");

    [Fact]
    public Task Delete_DecomposedUnicode_PreservesLiteralIdentity() => AssertLiteralAsync("cafe\u0301.txt");

    [Fact]
    public Task Delete_Backslash_PreservesLiteralIdentity() => AssertLiteralAsync("folder\\file.txt");

    [Fact]
    public Task Delete_LeadingSlash_PreservesLiteralIdentity() => AssertLiteralAsync("/folder/file.txt");

    [Fact]
    public Task Delete_RepeatedSlash_PreservesLiteralIdentity() => AssertLiteralAsync("folder//file.txt");

    [Fact]
    public Task Delete_TrailingSlash_PreservesLiteralIdentity() => AssertLiteralAsync("folder/file.txt/");

    [Fact]
    public Task Delete_Punctuation_PreservesLiteralIdentity() => AssertLiteralAsync("folder/%?# +.txt");

    [Fact]
    public Task Delete_Thai_PreservesLiteralIdentity() => AssertLiteralAsync("ชิ้นงาน.step");

    [Fact]
    public Task Delete_ThaiDecomposedLiteral_PreservesLiteralIdentity() => AssertLiteralAsync("ชิ้นงาน/cafe\u0301.step");

    [Fact]
    public Task Delete_ControlPrefix_RejectsBeforeBoundaryCalls() => AssertRejectedAsync("\tfile.txt");

    [Fact]
    public Task Delete_ControlSuffix_RejectsBeforeBoundaryCalls() => AssertRejectedAsync("file.txt\n");

    [Fact]
    public Task Delete_ParentTraversal_RejectsBeforeBoundaryCalls() => AssertRejectedAsync("folder/../file.txt");

    [Fact]
    public Task Delete_CurrentTraversal_RejectsBeforeBoundaryCalls() => AssertRejectedAsync("./file.txt");

    [Fact]
    public Task Delete_ReservedSlashAlias_RejectsBeforeBoundaryCalls() => AssertRejectedAsync(" /customer-documents//file.txt ");

    [Fact]
    public Task Delete_ReservedBackslashAlias_RejectsBeforeBoundaryCalls() => AssertRejectedAsync("customer-documents\\file.txt");

    [Fact]
    public Task Delete_MissingMetadata_PreservesMetadata() => AssertFailureAsync(false, false);

    [Fact]
    public Task Delete_ProviderFailure_PreservesMetadata() => AssertFailureAsync(true, false);

    private static async Task AssertLiteralAsync(string objectName)
    {
        var storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        var repository = new Mock<IUploadRepository>(MockBehavior.Strict);
        var token = CancellationToken.None;
        repository.Setup(x => x.ExistsAsync("maliev.com", objectName, token)).ReturnsAsync(true);
        storage.Setup(x => x.DeleteAsync("maliev.com", objectName, token)).ReturnsAsync(true);
        repository.Setup(x => x.DeleteAsync("maliev.com", objectName, token)).Returns(Task.CompletedTask);

        Assert.True(await Create(storage.Object, repository.Object).DeleteAsync("maliev.com", objectName, token));

        repository.VerifyAll();
        storage.VerifyAll();
        repository.VerifyNoOtherCalls();
        storage.VerifyNoOtherCalls();
    }

    private static async Task AssertRejectedAsync(string objectName)
    {
        var storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        var repository = new Mock<IUploadRepository>(MockBehavior.Strict);

        await Assert.ThrowsAsync<FileUploadValidationException>(() =>
            Create(storage.Object, repository.Object).DeleteAsync("maliev.com", objectName, CancellationToken.None));

        repository.VerifyNoOtherCalls();
        storage.VerifyNoOtherCalls();
    }

    private static async Task AssertFailureAsync(bool metadataExists, bool providerResult)
    {
        const string objectName = " literal.txt ";
        var storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        var repository = new Mock<IUploadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.ExistsAsync("maliev.com", objectName, CancellationToken.None)).ReturnsAsync(metadataExists);
        if (metadataExists)
        {
            storage.Setup(x => x.DeleteAsync("maliev.com", objectName, CancellationToken.None)).ReturnsAsync(providerResult);
        }

        Assert.False(await Create(storage.Object, repository.Object).DeleteAsync("maliev.com", objectName, CancellationToken.None));

        repository.VerifyAll();
        storage.VerifyAll();
        repository.VerifyNoOtherCalls();
        storage.VerifyNoOtherCalls();
    }

    private static FileApplicationService Create(IObjectStorage storage, IUploadRepository repository)
    {
        var options = Options.Create(new FileStorageOptions
        {
            Enabled = true,
            WritesEnabled = true,
            AllowedBuckets = ["maliev.com"],
        });
        return new FileApplicationService(storage,
            Mock.Of<IFileSafetyScanner>(), repository, Mock.Of<IStorageMoveJournal>(),
            new ObjectNamePolicy(options, TimeProvider.System), options, new LegacyFileRuntimeGate(options),
            NullLogger<FileApplicationService>.Instance, Mock.Of<IQuarantineUploadIntent>(),
            new UploadSnapshotCapture(), Mock.Of<IStorageReadJournal>());
    }
}
