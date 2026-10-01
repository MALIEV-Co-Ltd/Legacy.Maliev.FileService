using System.Reflection;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;
using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Legacy.Maliev.FileService.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Controllers;

public sealed class FileControllerContractTests
{
    [Fact]
    public void UploadsController_PreservesLegacyRouteAndAuthenticatedMethods()
    {
        Assert.Equal("[controller]", typeof(UploadsController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(UploadsController).GetCustomAttribute<AuthorizeAttribute>());
        AssertMethodContract(nameof(UploadsController.UploadAsync), typeof(HttpPostAttribute), FilePermissions.Create);
        AssertMethodContract(nameof(UploadsController.MoveUploadAsync), typeof(HttpPutAttribute), FilePermissions.Update);
        AssertMethodContract(nameof(UploadsController.DeleteUploadAsync), typeof(HttpDeleteAttribute), FilePermissions.Delete);
    }

    [Fact]
    public void SignedUrlController_PreservesLegacyRouteAndAuthentication()
    {
        Assert.Equal("uploads/[controller]", typeof(SignedUrlController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(SignedUrlController).GetCustomAttribute<AuthorizeAttribute>());
        AssertMethodContract(nameof(SignedUrlController.GetSignedUrlAsync), typeof(HttpGetAttribute), FilePermissions.Read);
    }

    [Fact]
    public void UploadResponse_PreservesPascalCaseWireShape()
    {
        var objectProperty = typeof(UploadResultResponse).GetProperty("Object");
        var responseProperties = typeof(UploadObjectResponse).GetProperties().Select(property => property.Name).ToArray();

        Assert.NotNull(objectProperty);
        Assert.Equal(["Bucket", "ObjectName", "Uri"], responseProperties);
    }

    [Fact]
    public void UploadAsync_AcceptsLegacyCompatibleIdempotencyHeader()
    {
        var method = typeof(UploadsController).GetMethod(nameof(UploadsController.UploadAsync))!;
        var parameter = Assert.Single(method.GetParameters(), value =>
            value.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key");

        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.True(parameter.HasDefaultValue);
        Assert.Null(parameter.DefaultValue);
    }

    [Fact]
    public async Task KeyedUpload_RequiresStableSignedServicePrincipal()
    {
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired, "reservation")));
        var result = await controller.UploadAsync("maliev.com", Files(), null, default, "workflow-42");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Theory]
    [InlineData(UploadAcquireState.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(UploadAcquireState.InProgress, StatusCodes.Status409Conflict)]
    [InlineData(UploadAcquireState.Unknown, StatusCodes.Status503ServiceUnavailable)]
    public async Task KeyedUpload_MapsReplayStatesWithoutExecutingService(UploadAcquireState state, int status)
    {
        var service = new Mock<IFileService>(); var controller = Controller(new StubStore(new(state)), service);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", "intranet-service")], "test"));
        var result = await controller.UploadAsync("maliev.com", Files(), null, default, "workflow-42");
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        service.Verify(value => value.UploadAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<IUploadFile>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KeyedUpload_DisabledRuntime_ReturnsUnavailableBeforeReadingBodyOrTouchingStore()
    {
        var store = new StubStore(new(UploadAcquireState.Acquired, "reservation"));
        var service = new Mock<IFileService>();
        var file = new Mock<IFormFile>();
        file.SetupGet(value => value.FileName).Returns("part.stl");
        file.SetupGet(value => value.Length).Returns(1);
        file.SetupGet(value => value.ContentType).Returns("model/stl");
        var controller = Controller(store, service, enabled: false, writesEnabled: true);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("client_id", "intranet-service")], "test"));

        var result = await controller.UploadAsync("maliev.com", [file.Object], null, default, "workflow-42");

        AssertUnavailable(result.Result);
        Assert.Equal(0, store.AcquireCalls);
        file.Verify(value => value.OpenReadStream(), Times.Never);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnkeyedUpload_RollbackFailure_ReturnsGenericUnavailableProblemWithoutObjectCoordinates()
    {
        var service = new Mock<IFileService>();
        service.Setup(value => value.UploadAsync(
                "maliev.com",
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<IUploadFile>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UploadRollbackException(
                new InvalidOperationException("signing unavailable"),
                [new UploadCleanupFailure("maliev.com", "orders/private/part.step", new IOException("delete unavailable"))]));
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired)), service);

        var result = await controller.UploadAsync("maliev.com", Files(), null, CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal("Upload outcome unknown", problem.Title);
        Assert.Equal("Upload outcome requires reconciliation.", problem.Detail);
        Assert.DoesNotContain("maliev.com", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("part.step", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("delete unavailable", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteUploadAsync_DisabledDependency_ReturnsUnavailableProblem()
    {
        var service = new Mock<IFileService>();
        service.Setup(value => value.DeleteAsync("maliev.com", "part.stl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MalwareScannerUnavailableException("Legacy file writes are disabled."));
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired)), service);

        var result = await controller.DeleteUploadAsync("maliev.com", "part.stl", default);

        AssertUnavailable(result);
    }

    [Fact]
    public async Task MoveUploadAsync_DisabledDependency_ReturnsUnavailableProblem()
    {
        var service = new Mock<IFileService>();
        service.Setup(value => value.MoveAsync("source", "part.stl", "destination", "part.stl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MalwareScannerUnavailableException("Legacy file writes are disabled."));
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired)), service);

        var result = await controller.MoveUploadAsync("source", "part.stl", "destination", "part.stl", default);

        AssertUnavailable(result);
    }

    [Fact]
    public async Task MoveUploadAsync_UnknownStorageOutcome_ReturnsRedactedUnavailableProblem()
    {
        var service = new Mock<IFileService>();
        service.Setup(value => value.MoveAsync("source", "private/part.stl", "destination", "private/part.stl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UploadOutcomeUnknownException("Storage response for private/part.stl was lost."));
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired)), service);

        var result = await controller.MoveUploadAsync("source", "private/part.stl", "destination", "private/part.stl", default);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal("Move outcome unknown", problem.Title);
        Assert.Equal("Move outcome requires reconciliation.", problem.Detail);
        Assert.DoesNotContain("part.stl", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveUploadAsync_RollbackFailure_ReturnsRedactedUnavailableProblem()
    {
        var service = new Mock<IFileService>();
        service.Setup(value => value.MoveAsync("source", "private/part.stl", "destination", "private/part.stl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UploadRollbackException(
                new IOException("source delete unavailable"),
                [new UploadCleanupFailure("destination", "private/part.stl", new IOException("rollback unavailable"))]));
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired)), service);

        var result = await controller.MoveUploadAsync("source", "private/part.stl", "destination", "private/part.stl", default);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal("Move outcome unknown", problem.Title);
        Assert.Equal("Move outcome requires reconciliation.", problem.Detail);
        Assert.DoesNotContain("part.stl", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSignedUrlAsync_DisabledDependency_ReturnsUnavailableProblem()
    {
        var service = new Mock<IFileService>();
        service.Setup(value => value.GetSignedUrlAsync("maliev.com", "part.stl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MalwareScannerUnavailableException("Legacy file writes are disabled."));
        var controller = new SignedUrlController(service.Object);

        var result = await controller.GetSignedUrlAsync("maliev.com", "part.stl", default);

        AssertUnavailable(result.Result);
    }

    private static UploadsController Controller(
        IUploadIdempotencyStore store,
        Mock<IFileService>? service = null,
        bool enabled = true,
        bool writesEnabled = true) =>
        new(
            (service ?? new Mock<IFileService>()).Object,
            new IdempotentUploadCoordinator(store, new UploadSnapshotCapture()),
            new LegacyFileRuntimeGate(Options.Create(new FileStorageOptions
            {
                Enabled = enabled,
                WritesEnabled = writesEnabled,
            })))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static void AssertUnavailable(IActionResult? result)
    {
        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal("Legacy file service unavailable", problem.Title);
    }
    private static List<IFormFile> Files() => [new FormFile(new MemoryStream([1]), 0, 1, "files", "part.stl") { Headers = new HeaderDictionary(), ContentType = "model/stl" }];
    private sealed class StubStore(UploadAcquireResult result) : IUploadIdempotencyStore
    {
        public int AcquireCalls { get; private set; }
        public Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, string effectivePath, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return Task.FromResult(result with { EffectivePath = result.EffectivePath ?? effectivePath });
        }
        public Task<bool> RenewAsync(string identity, string reservationId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteAsync(string identity, string fingerprint, string reservationId, UploadResultResponse response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkUnknownAsync(string identity, string reservationId, UploadResultResponse? response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseAsync(string identity, string reservationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void AssertMethodContract(string name, Type methodAttribute, string permission)
    {
        var method = typeof(UploadsController).GetMethod(name) ?? typeof(SignedUrlController).GetMethod(name);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttributes().SingleOrDefault(attribute => attribute.GetType() == methodAttribute));
        var permissionAttribute = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(permission, permissionAttribute.Permission);
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class FileControllerMoveCheckpointTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Uploads_SourceAbsentCheckpointFails_ReturnsUnknownWithoutSigningOrMetadata(
        bool publicMove, bool canceled, bool unknownCheckpointFails)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        var prefix = $"checkpoint/{Guid.NewGuid():N}";
        var sourceName = $"{prefix}/source.stl";
        var destinationName = $"{prefix}/part.stl";
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        StorageMoveJournal? priorProof = null;
        if (publicMove)
        {
            context.Uploads.Add(new Upload
            {
                Bucket = "private",
                Name = sourceName,
                Size = 1,
                ContentType = "model/stl",
                CreatedDate = created,
                ModifiedDate = created
            });
            await context.SaveChangesAsync();
            // Synthetic prerequisite only: prior committed clean lineage must match
            // the controlled live source generation17, not a fabricated name-only grant.
            // Missing lineage refusal is covered by actual Production HTTP controls.
            var priorId = Guid.NewGuid();
            var priorJournal = new StorageMoveJournalRepository(context, TimeProvider.System);
            Assert.True(await priorJournal.BeginAsync(priorId, true, "private", $"_quarantine/{priorId:N}/{sourceName}", 5,
                "private", sourceName, default));
            await priorJournal.CopiedAsync(priorId, 17, default);
            await priorJournal.SourceDeletedAsync(priorId, default);
            await priorJournal.MetadataCommittedAsync(priorId, default);
            priorProof = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.OperationId == priorId);
        }

        using var request = new CancellationTokenSource();
        var journal = new FailingDeletionCheckpoint(new StorageMoveJournalRepository(context, TimeProvider.System), request, canceled, unknownCheckpointFails);
        var objects = new Dictionary<string, long>();
        if (publicMove) objects[sourceName] = 17;
        var deleteCalls = new List<string>();
        var client = new Mock<StorageClient>(MockBehavior.Strict);
        client.Setup(value => value.UploadObjectAsync(It.IsAny<StorageObject>(), It.IsAny<Stream>(),
                It.Is<UploadObjectOptions>(options => options.IfGenerationMatch == 0), It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(invocation =>
            {
                var item = (StorageObject)invocation.Arguments[0];
                Assert.Equal("private", item.Bucket);
                Assert.StartsWith("_quarantine/", item.Name, StringComparison.Ordinal);
                Assert.True(objects.TryAdd(item.Name, 17));
                return Task.FromResult(new StorageObject { Generation = 17, Size = 1 });
            }));
        client.Setup(value => value.GetObjectAsync("private", It.IsAny<string>(), It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, GetObjectOptions, CancellationToken>((_, name, _, _) =>
                Task.FromResult(new StorageObject { Generation = objects[name], Size = 1 }));
        client.Setup(value => value.CopyObjectAsync("private", It.IsAny<string>(), "private", destinationName,
                It.Is<CopyObjectOptions>(options => options.SourceGeneration == 17 && options.IfSourceGenerationMatch == 17 && options.IfGenerationMatch == 0),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, string, string, CopyObjectOptions, CancellationToken>((_, source, _, destination, _, _) =>
            {
                Assert.Equal(17, objects[source]);
                Assert.True(objects.TryAdd(destination, 31));
                // Another actor already deleted the observed source after the copy committed.
                Assert.True(objects.Remove(source));
                return Task.FromResult(new StorageObject { Generation = 31, Size = 1 });
            });
        client.Setup(value => value.DeleteObjectAsync("private", It.IsAny<string>(),
                It.Is<DeleteObjectOptions>(options => options.IfGenerationMatch == 17), It.IsAny<CancellationToken>()))
            .Returns<string, string, DeleteObjectOptions, CancellationToken>((_, name, _, _) =>
            {
                deleteCalls.Add(name);
                Assert.False(objects.ContainsKey(name));
                return Task.FromException(new GoogleApiException("storage", "source absent") { HttpStatusCode = HttpStatusCode.NotFound });
            });
        var scanner = new Mock<IFileSafetyScanner>(MockBehavior.Strict);
        scanner.Setup(value => value.ScanAsync(It.IsAny<IUploadFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSafetyResult(FileSafetyVerdict.Clean));
        var options = Options.Create(new FileStorageOptions { Enabled = true, WritesEnabled = true, AllowedBuckets = ["private"] });
        var gate = new LegacyFileRuntimeGate(options);
        var signingCalls = 0;
        var blobSigner = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
        blobSigner.SetupGet(value => value.Id).Returns("controlled@example.invalid");
        blobSigner.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
        blobSigner.Setup(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()))
            .Callback(() => signingCalls++).ReturnsAsync("AQ==");
        var snapshots = new UploadSnapshotCapture();
        var service = new FileApplicationService(new GoogleCloudObjectStorage(client.Object, UrlSigner.FromBlobSigner(blobSigner.Object), journal), scanner.Object,
            new UploadRepository(context, TimeProvider.System), journal, new ObjectNamePolicy(options, TimeProvider.System),
            options, gate, NullLogger<FileApplicationService>.Instance, new QuarantineUploadIntentRepository(context, TimeProvider.System), snapshots);
        var controller = new UploadsController(service, new IdempotentUploadCoordinator(new Mock<IUploadIdempotencyStore>(MockBehavior.Strict).Object, snapshots), gate)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        IActionResult? result;
        if (publicMove)
            result = await controller.MoveUploadAsync("private", sourceName, "private", destinationName, request.Token);
        else
        {
            var file = new FormFile(new MemoryStream([1]), 0, 1, "files", "part.stl")
            { Headers = new HeaderDictionary(), ContentType = "model/stl" };
            result = (await controller.UploadAsync("private", [file], prefix, request.Token)).Result;
        }

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal(publicMove ? "Move outcome unknown" : "Upload outcome unknown", problem.Title);
        Assert.Contains("requires reconciliation", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(prefix, problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint unavailable", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(0, signingCalls);
        Assert.Single(deleteCalls);
        Assert.NotEqual(destinationName, deleteCalls[0]);
        Assert.Equal(31, Assert.Single(objects).Value);
        Assert.Equal(destinationName, Assert.Single(objects).Key);
        await using var readback = fixture.CreateContext();
        var evidence = await readback.StorageMoveJournals.AsNoTracking().SingleAsync(value => value.DestinationObjectName == destinationName);
        Assert.True(evidence.ScanClean);
        Assert.Equal(17, evidence.SourceGeneration);
        Assert.Equal(31, evidence.DestinationGeneration);
        Assert.Equal(unknownCheckpointFails ? "Copied" : "Unknown", evidence.State);
        Assert.Equal(deleteCalls[0], evidence.SourceObjectName);
        Assert.False(await readback.Uploads.AnyAsync(value => value.Name == destinationName));
        if (publicMove)
        {
            var upload = await readback.Uploads.SingleAsync(value => value.Name == sourceName);
            Assert.Equal(created, upload.CreatedDate);
            Assert.Equal(created, upload.ModifiedDate);
            var retained = await readback.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.OperationId == priorProof!.OperationId);
            Assert.Equivalent(priorProof, retained, strict: true);
        }
    }

    private sealed class FailingDeletionCheckpoint(
        IStorageMoveJournal inner, CancellationTokenSource request, bool canceled, bool unknownCheckpointFails) : IStorageMoveJournal
    {
        public Task<bool> TryBeginMetadataSubmissionAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken token) => inner.TryBeginMetadataSubmissionAsync(claims, token);
        public Task<bool> TryBeginCompensationAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken token) => inner.TryBeginCompensationAsync(claims, token);
        public Task MetadataSubmissionCommittedAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken token) => inner.MetadataSubmissionCommittedAsync(claims, token);
        public Task RecordCompensationAsync(StorageMoveClaim claim, CompensationDisposition disposition, CancellationToken token) => inner.RecordCompensationAsync(claim, disposition, token);
        public Task<StorageMoveEvidence?> FindCommittedSourceAsync(string bucket, string name, CancellationToken token) => inner.FindCommittedSourceAsync(bucket, name, token);
        public Task<StorageMoveEvidence?> FindAsync(Guid operationId, CancellationToken cancellationToken) => inner.FindAsync(operationId, cancellationToken);
        public Task<bool> BeginAsync(Guid operationId, bool scanClean, string sourceBucket, string sourceObjectName, long sourceGeneration,
            string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
            inner.BeginAsync(operationId, scanClean, sourceBucket, sourceObjectName, sourceGeneration, destinationBucket, destinationObjectName, cancellationToken);
        public Task CopiedAsync(Guid operationId, long destinationGeneration, CancellationToken cancellationToken) => inner.CopiedAsync(operationId, destinationGeneration, cancellationToken);
        public Task SourceDeletedAsync(Guid operationId, CancellationToken cancellationToken)
        {
            if (canceled) request.Cancel();
            return Task.FromException(canceled ? new OperationCanceledException(request.Token) : new IOException("checkpoint unavailable"));
        }
        public Task MetadataCommittedAsync(Guid operationId, CancellationToken cancellationToken) => inner.MetadataCommittedAsync(operationId, cancellationToken);
        public Task UnknownAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            Assert.False(cancellationToken.IsCancellationRequested);
            Assert.NotEqual(request.Token, cancellationToken);
            return unknownCheckpointFails
                ? Task.FromException(new IOException("unknown checkpoint unavailable"))
                : inner.UnknownAsync(operationId, cancellationToken);
        }
    }
}
