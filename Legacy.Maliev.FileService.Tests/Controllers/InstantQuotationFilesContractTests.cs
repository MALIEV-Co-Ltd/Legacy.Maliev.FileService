using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.FileService.Tests.Controllers;

public sealed class InstantQuotationFilesContractTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid QuotationRequestId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Controller_PublishesExactAuthenticatedRoutesAndPermission()
    {
        Assert.Equal("file/v1/instant-quotation", typeof(InstantQuotationFilesController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(InstantQuotationFilesController).GetCustomAttribute<AuthorizeAttribute>());

        AssertMethod(nameof(InstantQuotationFilesController.CreateSessionAsync), "sessions");
        AssertMethod(nameof(InstantQuotationFilesController.UploadAsync), "sessions/{sessionId}/files");
        AssertMethod(nameof(InstantQuotationFilesController.FinalizeAsync), "sessions/{sessionId}/finalizations");
    }

    [Fact]
    public void Upload_RequiresExactHeadersAndDoesNotUseIFormFile()
    {
        var method = typeof(InstantQuotationFilesController).GetMethod(nameof(InstantQuotationFilesController.UploadAsync))!;
        var headers = method.GetParameters()
            .Select(parameter => parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        Assert.Equal(["X-Quote-Session-Token", "Idempotency-Key", "X-Content-SHA256"], headers);
        Assert.DoesNotContain(method.GetParameters(), parameter =>
            parameter.ParameterType == typeof(IFormFile) ||
            parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericArguments().Contains(typeof(IFormFile)));
    }

    [Fact]
    public void Contracts_SerializeSuccessJsonAsCamelCase()
    {
        var response = new CreateInstantQuoteSessionResponse(SessionId, "opaque-token", DateTimeOffset.Parse("2026-07-18T12:00:00Z"));
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"sessionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sessionToken\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expiresAt\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SessionId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_PublishesExactSupportedExtensionsAndLimit()
    {
        Assert.Equal(
            [".stl", ".obj", ".3mf", ".step", ".stp", ".iges", ".igs", ".glb", ".gltf"],
            InstantQuoteFileContract.SupportedExtensions);
        Assert.Equal(200L * 1024 * 1024, InstantQuoteFileContract.MaximumUploadBytes);
    }

    [Fact]
    public async Task CreateSession_Returns201AndPassesClaimOwner()
    {
        var service = new StubService();
        var controller = Controller(service);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "customer-42")], "test"));

        var result = await controller.CreateSessionAsync(default);

        var created = Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal("customer-42", service.Owner?.PrincipalId);
        Assert.True(service.Owner?.IsAuthenticated);
    }

    [Fact]
    public async Task Upload_ReadsExactlyOneFilesPartAndReturns201()
    {
        var service = new StubService();
        var reader = new StubMultipartReader();
        var controller = Controller(service, reader);

        var result = await controller.UploadAsync(SessionId, "token", "upload-1", "ABC123", default);

        var created = Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal("files", reader.RequiredPartName);
        Assert.Equal("part.stl", service.UploadMetadata?.FileName);
    }

    [Fact]
    public async Task Finalize_Returns200AndForwardsSelectedFileIds()
    {
        var service = new StubService();
        var controller = Controller(service);
        var request = new FinalizeInstantQuoteFilesRequest(QuotationRequestId, [FileId]);

        var result = await controller.FinalizeAsync(SessionId, "token", "finalize-1", request, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        Assert.Equal(FileId, Assert.Single(service.FinalizeRequest!.FileIds));
    }

    [Theory]
    [InlineData(typeof(InstantQuoteValidationException), StatusCodes.Status400BadRequest, "validation_error")]
    [InlineData(typeof(InstantQuoteOwnershipException), StatusCodes.Status403Forbidden, "session_forbidden")]
    [InlineData(typeof(InstantQuoteReplayConflictException), StatusCodes.Status409Conflict, "idempotency_conflict")]
    [InlineData(typeof(InstantQuotePayloadTooLargeException), StatusCodes.Status413PayloadTooLarge, "payload_too_large")]
    [InlineData(typeof(InstantQuoteDependencyUnavailableException), StatusCodes.Status503ServiceUnavailable, "dependency_unavailable")]
    [InlineData(typeof(InstantQuoteAmbiguousOutcomeException), StatusCodes.Status503ServiceUnavailable, "outcome_unknown")]
    public async Task Exceptions_MapToStableProblemDetails(Type exceptionType, int expectedStatus, string expectedCode)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "secret token must not leak")!;
        var controller = Controller(new StubService(exception));

        var result = await controller.CreateSessionAsync(default);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Equal(expectedCode, problem.Extensions["code"]);
        Assert.DoesNotContain("secret token", JsonSerializer.Serialize(problem), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_IsNotConvertedToProblemDetails()
    {
        var controller = Controller(new StubService(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.CreateSessionAsync(default));
    }

    private static InstantQuotationFilesController Controller(
        IInstantQuoteFileService service,
        IInstantQuoteMultipartReader? reader = null)
    {
        return new(service, reader ?? new StubMultipartReader())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static void AssertMethod(string name, string template)
    {
        var method = typeof(InstantQuotationFilesController).GetMethod(name)!;
        Assert.Equal(template, method.GetCustomAttribute<HttpPostAttribute>()?.Template);
        var permission = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal("legacy-file.uploads.create", permission.Permission);
    }

    private sealed class StubMultipartReader : IInstantQuoteMultipartReader
    {
        public string? RequiredPartName { get; private set; }

        public Task<InstantQuoteMultipartFile> ReadSingleAsync(
            HttpRequest request,
            string requiredPartName,
            CancellationToken cancellationToken)
        {
            RequiredPartName = requiredPartName;
            return Task.FromResult(new InstantQuoteMultipartFile(
                new MemoryStream([1, 2, 3]),
                new InstantQuoteUploadMetadata("part.stl", "model/stl")));
        }
    }

    private sealed class StubService(Exception? createException = null) : IInstantQuoteFileService
    {
        public InstantQuoteOwner? Owner { get; private set; }
        public InstantQuoteUploadMetadata? UploadMetadata { get; private set; }
        public FinalizeInstantQuoteFilesRequest? FinalizeRequest { get; private set; }

        public Task<CreateInstantQuoteSessionResponse> CreateInstantQuoteSessionAsync(
            InstantQuoteOwner owner,
            CancellationToken cancellationToken)
        {
            if (createException is not null)
            {
                throw createException;
            }

            Owner = owner;
            return Task.FromResult(new CreateInstantQuoteSessionResponse(
                SessionId,
                "opaque-token",
                DateTimeOffset.Parse("2026-07-18T12:00:00Z")));
        }

        public Task<InstantQuoteFileResponse> UploadAsync(
            Guid sessionId,
            string token,
            string idempotencyKey,
            string expectedSha256,
            Stream body,
            InstantQuoteUploadMetadata metadata,
            CancellationToken cancellationToken)
        {
            UploadMetadata = metadata;
            return Task.FromResult(new InstantQuoteFileResponse(
                FileId,
                metadata.FileName,
                metadata.ContentType,
                3,
                expectedSha256,
                "clean"));
        }

        public Task<FinalizeInstantQuoteFilesResponse> FinalizeAsync(
            Guid sessionId,
            string token,
            string idempotencyKey,
            FinalizeInstantQuoteFilesRequest request,
            CancellationToken cancellationToken)
        {
            FinalizeRequest = request;
            return Task.FromResult(new FinalizeInstantQuoteFilesResponse(request.QuotationRequestId, [
                new InstantQuoteFileResponse(FileId, "part.stl", "model/stl", 3, "ABC123", "finalized"),
            ]));
        }
    }
}
