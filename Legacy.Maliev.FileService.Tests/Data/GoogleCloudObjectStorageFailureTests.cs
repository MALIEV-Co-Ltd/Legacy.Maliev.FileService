using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Moq;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class GoogleCloudObjectStorageFailureTests
{
    [Fact]
    public async Task UploadGenerationAsync_QuarantineWrite_RequiresAbsentDestinationAndReturnsGeneration()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        client.Setup(value => value.UploadObjectAsync(
                It.Is<StorageObject>(item => item.Bucket == "private" && item.Name == "quarantine/file"),
                It.IsAny<Stream>(), It.Is<UploadObjectOptions>(options => options.IfGenerationMatch == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Generation = 17 });
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        Assert.Equal(17, await storage.UploadGenerationAsync("private", "quarantine/file", "application/octet-stream",
            new MemoryStream([1, 2, 3]), default));
    }

    [Fact]
    public async Task MoveJournaledAsync_QuarantineGenerationReplaced_DoesNotClaimCleanOrCopy()
    {
        var id = Guid.NewGuid();
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        journal.Setup(value => value.BeginAsync(id, true, "private", "quarantine/file", 18,
            "private", "clean/file", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        journal.Setup(value => value.UnknownAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        var storage = new GoogleCloudObjectStorage(client.Object, null!, journal.Object);

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveJournaledAsync(id, 18, true, "private", "quarantine/file", "private", "clean/file", default));

        journal.Verify(value => value.UnknownAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(value => value.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveJournaledAsync_CopySuccess_RecordsSourceBeforeCopyAndExactDestinationBeforeDelete()
    {
        var order = new List<string>();
        var id = Guid.NewGuid();
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        journal.Setup(value => value.BeginAsync(id, true, "private", "quarantine/file", 17,
                "private", "clean/file", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("begin")).ReturnsAsync(true);
        journal.Setup(value => value.CopiedAsync(id, 31, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("copied")).Returns(Task.CompletedTask);
        journal.Setup(value => value.SourceDeletedAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("deleted")).Returns(Task.CompletedTask);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(value => value.CopyObjectAsync("private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("copy")).ReturnsAsync(new StorageObject { Generation = 31 });
        client.Setup(value => value.DeleteObjectAsync("private", "quarantine/file",
                It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("delete")).Returns(Task.CompletedTask);

        var storage = new GoogleCloudObjectStorage(client.Object, null!, journal.Object);
        Assert.True(await storage.MoveJournaledAsync(id, 17, true, "private", "quarantine/file", "private", "clean/file", default));
        Assert.Equal(["begin", "copy", "copied", "delete", "deleted"], order);
    }

    [Fact]
    public async Task MoveJournaledAsync_LostCopyResponse_RetainsSourceEvidenceWithoutDeletion()
    {
        var id = Guid.NewGuid();
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        journal.Setup(value => value.BeginAsync(id, true, "private", "quarantine/file", 17,
            "private", "clean/file", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        journal.Setup(value => value.UnknownAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(value => value.CopyObjectAsync("private", "quarantine/file", "private", "clean/file",
            It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException());

        var storage = new GoogleCloudObjectStorage(client.Object, null!, journal.Object);
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveJournaledAsync(id, 17, true, "private", "quarantine/file", "private", "clean/file", default));
        journal.Verify(value => value.UnknownAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(value => value.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveJournaledAsync_ReplayedOperation_DoesNotCopyOrDelete()
    {
        var id = Guid.NewGuid();
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        journal.Setup(value => value.BeginAsync(id, true, "private", "quarantine/file", 17,
            "private", "clean/file", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);

        var storage = new GoogleCloudObjectStorage(client.Object, null!, journal.Object);
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveJournaledAsync(id, 17, true, "private", "quarantine/file", "private", "clean/file", default));
        client.Verify(value => value.CopyObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveJournaledAsync_CopyForbidden_LeavesDurableUnknownWithoutDeleting()
    {
        var id = Guid.NewGuid();
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        journal.Setup(value => value.BeginAsync(id, true, "private", "quarantine/file", 17,
            "private", "clean/file", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        journal.Setup(value => value.UnknownAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(value => value.CopyObjectAsync("private", "quarantine/file", "private", "clean/file",
            It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(ApiException(HttpStatusCode.Forbidden));

        var storage = new GoogleCloudObjectStorage(client.Object, null!, journal.Object);
        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveJournaledAsync(id, 17, true, "private", "quarantine/file", "private", "clean/file", default));
        Assert.IsType<GoogleApiException>(failure.InnerException);
        journal.Verify(value => value.UnknownAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(value => value.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveJournaledAsync_RollbackForbidden_PreservesBothExactGenerations()
    {
        var id = Guid.NewGuid();
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        journal.Setup(value => value.BeginAsync(id, true, "private", "quarantine/file", 17,
            "private", "clean/file", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        journal.Setup(value => value.CopiedAsync(id, 31, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        journal.Setup(value => value.UnknownAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(value => value.CopyObjectAsync("private", "quarantine/file", "private", "clean/file",
            It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Generation = 31 });
        client.Setup(value => value.DeleteObjectAsync("private", "quarantine/file",
            It.Is<DeleteObjectOptions>(options => options.IfGenerationMatch == 17), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.Forbidden));
        client.Setup(value => value.DeleteObjectAsync("private", "clean/file",
            It.Is<DeleteObjectOptions>(options => options.Generation == 31 && options.IfGenerationMatch == 31),
            It.IsAny<CancellationToken>())).ThrowsAsync(ApiException(HttpStatusCode.Forbidden));

        var storage = new GoogleCloudObjectStorage(client.Object, null!, journal.Object);
        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveJournaledAsync(id, 17, true, "private", "quarantine/file", "private", "clean/file", default));

        Assert.IsType<UploadRollbackException>(failure.InnerException);
        journal.Verify(value => value.UnknownAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MoveAsync_SourceAbsentBeforeCopy_ReturnsFalseWithoutMutation()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        client.Setup(storage => storage.GetObjectAsync(
                "private", "quarantine/file", It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.NotFound));
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        Assert.False(await storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));
        client.Verify(storage => storage.CopyObjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_CopyForbidden_RemainsDefiniteFailureWithoutDeletion()
    {
        var forbidden = ApiException(HttpStatusCode.Forbidden);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(forbidden);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        Assert.Same(forbidden, await Assert.ThrowsAsync<GoogleApiException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default)));
        client.Verify(storage => storage.DeleteObjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_SourceAlreadyAbsentAfterCopy_KeepsPromotedObject()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "clean/file", Generation = 31 });
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "quarantine/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.NotFound));
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        Assert.True(await storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));
        client.Verify(storage => storage.CopyObjectAsync(
            "private", "quarantine/file", "private", "clean/file",
            It.Is<CopyObjectOptions>(options => options.IfGenerationMatch == 0 && options.DestinationPredefinedAcl == null),
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(storage => storage.DeleteObjectAsync(
            "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_SourceDeleteForbidden_RollsBackOnlyCopiedGeneration()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "clean/file", Generation = 31 });
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "quarantine/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.Forbidden));
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<GoogleApiException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));

        Assert.Equal(HttpStatusCode.Forbidden, failure.HttpStatusCode);
        client.Verify(storage => storage.DeleteObjectAsync(
            "private", "clean/file",
            It.Is<DeleteObjectOptions>(options => options.Generation == 31 && options.IfGenerationMatch == 31),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MoveAsync_SourceAndRollbackForbidden_RetainsBothFailuresAndObjectCoordinate()
    {
        var sourceFailure = ApiException(HttpStatusCode.Forbidden);
        var rollbackFailure = ApiException(HttpStatusCode.Forbidden);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "clean/file", Generation = 31 });
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "quarantine/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(sourceFailure);
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(rollbackFailure);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<UploadRollbackException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));

        Assert.Same(sourceFailure, failure.UploadFailure);
        var cleanup = Assert.Single(failure.CleanupFailures);
        Assert.Equal(("private", "clean/file"), (cleanup.Bucket, cleanup.ObjectName));
        Assert.Same(rollbackFailure, cleanup.Cause);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_CopyOutcomeUncertain_PreservesBothObjectsForReconciliation(bool canceled)
    {
        Exception failureCause = canceled
            ? new OperationCanceledException("copy was canceled")
            : new TimeoutException("copy response was lost");
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failureCause);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));

        Assert.Same(failureCause, failure.InnerException);
        client.Verify(storage => storage.DeleteObjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task MoveAsync_SourceChangedAfterRead_PreservesCurrentSource(HttpStatusCode statusCode)
    {
        var conflict = ApiException(statusCode);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(conflict);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));

        Assert.Same(conflict, failure.InnerException);
        client.Verify(storage => storage.DeleteObjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_CopyWithoutGeneration_PreservesSourceAndDestination()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "clean/file" });
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));

        Assert.IsType<InvalidDataException>(failure.InnerException);
        client.Verify(storage => storage.DeleteObjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_SourceDeleteOutcomeUncertain_PreservesCopiedObjectForReconciliation(bool canceled)
    {
        Exception failureCause = canceled
            ? new OperationCanceledException("delete was canceled")
            : new TimeoutException("delete response was lost");
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "clean/file", Generation = 31 });
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "quarantine/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failureCause);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));

        Assert.Same(failureCause, failure.InnerException);
        client.Verify(storage => storage.DeleteObjectAsync(
            "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_SourceGenerationChanged_DoesNotDeleteNewerSource()
    {
        var conflict = ApiException(HttpStatusCode.PreconditionFailed);
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        SetupSource(client);
        client.Setup(storage => storage.CopyObjectAsync(
                "private", "quarantine/file", "private", "clean/file",
                It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "clean/file", Generation = 31 });
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "quarantine/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(conflict);
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            storage.MoveAsync("private", "quarantine/file", "private", "clean/file", default));
        Assert.Same(conflict, failure.InnerException);
        client.Verify(storage => storage.CopyObjectAsync(
            "private", "quarantine/file", "private", "clean/file",
            It.Is<CopyObjectOptions>(options => options.SourceGeneration == 17
                && options.IfSourceGenerationMatch == 17 && options.IfGenerationMatch == 0),
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(storage => storage.DeleteObjectAsync(
            "private", "quarantine/file",
            It.Is<DeleteObjectOptions>(options => options.Generation == null && options.IfGenerationMatch == 17),
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(storage => storage.DeleteObjectAsync(
            "private", "clean/file",
            It.Is<DeleteObjectOptions>(options => options.Generation == 31 && options.IfGenerationMatch == 31),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_StorageNotFound_IsTheOnlyMissingResult()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.NotFound));
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        Assert.False(await storage.DeleteAsync("private", "clean/file", default));
    }

    [Fact]
    public async Task DeleteAsync_StorageForbidden_RemainsActionableFailure()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        client.Setup(storage => storage.DeleteObjectAsync(
                "private", "clean/file", It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.Forbidden));
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<GoogleApiException>(() =>
            storage.DeleteAsync("private", "clean/file", default));

        Assert.Equal(HttpStatusCode.Forbidden, failure.HttpStatusCode);
    }

    [Fact]
    public async Task GetSizeAsync_StorageForbidden_RemainsActionableFailure()
    {
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        client.Setup(storage => storage.GetObjectAsync(
                "private", "clean/file", It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ApiException(HttpStatusCode.Forbidden));
        var storage = new GoogleCloudObjectStorage(client.Object, null!);

        var failure = await Assert.ThrowsAsync<GoogleApiException>(() =>
            storage.GetSizeAsync("private", "clean/file", default));

        Assert.Equal(HttpStatusCode.Forbidden, failure.HttpStatusCode);
    }

    private static GoogleApiException ApiException(HttpStatusCode statusCode) => new("storage", "failure")
    {
        HttpStatusCode = statusCode,
    };

    private static void SetupSource(Mock<StorageClient> client) =>
        client.Setup(storage => storage.GetObjectAsync(
                "private", "quarantine/file", It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageObject { Bucket = "private", Name = "quarantine/file", Generation = 17 });
}
