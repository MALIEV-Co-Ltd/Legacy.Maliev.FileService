using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.Controllers;

public sealed class InstantQuotationFilesContractTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const int QuotationRequestId = 417;

    [Fact]
    public void Controller_PublishesExactAuthenticatedRoutesAndPermission()
    {
        Assert.Equal("file/v1/instant-quotation", typeof(InstantQuotationFilesController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(InstantQuotationFilesController).GetCustomAttribute<AuthorizeAttribute>());

        AssertMethod(nameof(InstantQuotationFilesController.CreateSessionAsync), "sessions");
        AssertMethod(nameof(InstantQuotationFilesController.UploadAsync), "sessions/{sessionId}/files");
        AssertMethod(nameof(InstantQuotationFilesController.FinalizeAsync), "sessions/{sessionId}/finalizations");
        AssertMethod(nameof(InstantQuotationFilesController.RemoveAsync), "sessions/{sessionId}/files/{fileId}");
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
        var response = new CreateInstantQuoteSessionResponse(
            SessionId,
            "opaque-token",
            DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            InstantQuoteFileContract.MaximumUploadBytes,
            InstantQuoteFileContract.SupportedExtensions);
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"sessionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sessionToken\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expiresAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maxUploadBytes\":209715200", json, StringComparison.Ordinal);
        Assert.Contains("\"supportedExtensions\":[\".stl\",\".obj\",\".3mf\",\".step\",\".stp\",\".iges\",\".igs\",\".glb\",\".gltf\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SessionId\"", json, StringComparison.Ordinal);

        var finalized = JsonSerializer.Serialize(new FinalizedInstantQuoteFileResponse(
            FileId, "private-bucket", "instant-quotation/final", "part.stl", "model/stl", 3,
            new string('a', 64), "finalized"));
        Assert.Contains("\"bucket\":\"private-bucket\"", finalized, StringComparison.Ordinal);
        Assert.Contains("\"objectName\":\"instant-quotation/final\"", finalized, StringComparison.Ordinal);
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
    public async Task Documentation_FreezesRemovalErrorsCancellationAndAuthorityBoundary()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "contracts", "instant-quotation-files-v1.md"));
        var documentation = await File.ReadAllTextAsync(path);

        Assert.Contains("DELETE /file/v1/instant-quotation/sessions/{sessionId}/files/{fileId}", documentation, StringComparison.Ordinal);
        foreach (var value in new[] { "401", "403", "409", "413", "415", "422", "503" })
        {
            Assert.Contains(value, documentation, StringComparison.Ordinal);
        }
        Assert.Contains("unsupported_media_type", documentation, StringComparison.Ordinal);
        Assert.Contains("request abort", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GeometryService", documentation, StringComparison.Ordinal);
        Assert.Contains("authoritative geometry/DFM", documentation, StringComparison.Ordinal);
        Assert.Contains("never calculates geometry, DFM, or price", documentation, StringComparison.Ordinal);
        Assert.Contains("Temporary private object names", documentation, StringComparison.Ordinal);
        Assert.Contains("finalized bucket and objectName", documentation, StringComparison.Ordinal);
        Assert.Contains("platform_authentication_required", documentation, StringComparison.Ordinal);
        Assert.Contains("permission_forbidden", documentation, StringComparison.Ordinal);
        Assert.Contains("WWW-Authenticate: Bearer", documentation, StringComparison.Ordinal);
        Assert.Contains("Browsers do not call FileService", documentation, StringComparison.Ordinal);
        Assert.Contains("server-side session state", documentation, StringComparison.Ordinal);
        Assert.Contains("current Web integration does not delegate the member subject", documentation, StringComparison.Ordinal);
        Assert.Contains("Web service identity", documentation, StringComparison.Ordinal);
        Assert.Contains("required positive JSON integer", documentation, StringComparison.Ordinal);
        Assert.Contains("legacy `Request.ID`", documentation, StringComparison.Ordinal);
        Assert.Contains("`RequestFile.RequestID`", documentation, StringComparison.Ordinal);
        Assert.Contains("passes the same value unchanged", documentation, StringComparison.Ordinal);
        Assert.Contains("not a UUID", documentation, StringComparison.Ordinal);
        Assert.Contains("legacy database remains the source of truth", documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("33333333-3333-3333-3333-333333333333", documentation, StringComparison.Ordinal);
        foreach (var code in new[]
        {
            "validation_error", "session_forbidden", "idempotency_conflict", "upload_in_progress",
            "payload_too_large", "unsupported_media_type", "unsafe_content", "dependency_unavailable", "outcome_unknown",
        })
        {
            Assert.Contains(code, documentation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CreateSession_Returns201AndPassesClaimOwner()
    {
        var service = new StubService();
        var controller = Controller(service);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "customer-42", ClaimValueTypes.String, "https://issuer.example")], "test"));

        var result = await controller.CreateSessionAsync(default);

        var created = Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal("https://issuer.example|customer-42", service.Owner?.PrincipalId);
        Assert.True(service.Owner?.IsAuthenticated);
    }

    [Fact]
    public async Task CreateSession_PrefersIssuerQualifiedSubjectAndUsesClientFallbackOnlyWithoutSubject()
    {
        var userService = new StubService();
        var userController = Controller(userService);
        userController.ControllerContext.HttpContext.User = Principal(
            new Claim("sub", "customer-42", ClaimValueTypes.String, "https://issuer.example"),
            new Claim("client_id", "browser-client"),
            new Claim("azp", "authorized-party"));

        await userController.CreateSessionAsync(default);

        Assert.Equal("https://issuer.example|customer-42", userService.Owner?.PrincipalId);

        var clientService = new StubService();
        var clientController = Controller(clientService);
        clientController.ControllerContext.HttpContext.User = Principal(
            new Claim("client_id", "browser-client"),
            new Claim("azp", "authorized-party"));

        await clientController.CreateSessionAsync(default);

        Assert.Equal("browser-client", clientService.Owner?.PrincipalId);

        var authorizedPartyService = new StubService();
        var authorizedPartyController = Controller(authorizedPartyService);
        authorizedPartyController.ControllerContext.HttpContext.User = Principal(
            new Claim("azp", "authorized-party"));

        await authorizedPartyController.CreateSessionAsync(default);

        Assert.Equal("authorized-party", authorizedPartyService.Owner?.PrincipalId);
    }

    [Fact]
    public async Task CreateSession_PrefersIssuerQualifiedMappedNameIdentifierOverClientClaims()
    {
        var service = new StubService();
        var controller = Controller(service);
        controller.ControllerContext.HttpContext.User = Principal(
            new Claim("iss", "https://issuer.example"),
            new Claim(ClaimTypes.NameIdentifier, "mapped-customer-42"),
            new Claim("client_id", "browser-client"),
            new Claim("azp", "authorized-party"));

        await controller.CreateSessionAsync(default);

        Assert.Equal("https://issuer.example|mapped-customer-42", service.Owner?.PrincipalId);
    }

    [Fact]
    public async Task Pipeline_InvalidRequiredHeadersOrBody_ReturnsStableValidationProblem()
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        var requests = new[]
        {
            UploadRequestWithoutRequiredHeaders(),
            FinalizeRequest(content: null),
            FinalizeRequest(new StringContent("{", Encoding.UTF8, "application/json")),
        };

        foreach (var request in requests)
        {
            using (request)
            using (var response = await client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
                Assert.Equal("validation_error", problem.RootElement.GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task Upload_ReadsExactlyOneFilesPartAndReturns201()
    {
        var service = new StubService();
        var reader = new StubMultipartReader();
        var controller = Controller(service, reader);

        var result = await controller.UploadAsync(
            SessionId,
            new string('t', 32),
            new string('i', 16),
            new string('a', 64),
            default);

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

    [Fact]
    public async Task Remove_RequiresSessionTokenAndReturns204()
    {
        var service = new StubService();
        var controller = Controller(service);
        controller.ControllerContext.HttpContext.User = Principal(
            new Claim("sub", "customer-42", ClaimValueTypes.String, "https://issuer.example"));

        var result = await controller.RemoveAsync(SessionId, FileId, "token", default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(FileId, service.RemovedFileId);
        Assert.Equal("https://issuer.example|customer-42", service.RemoveOwner?.PrincipalId);
        var method = typeof(InstantQuotationFilesController).GetMethod(nameof(InstantQuotationFilesController.RemoveAsync))!;
        Assert.Contains(method.GetParameters(), parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == "X-Quote-Session-Token");
    }

    [Theory]
    [InlineData(typeof(InstantQuoteValidationException), StatusCodes.Status400BadRequest, "validation_error")]
    [InlineData(typeof(InstantQuoteOwnershipException), StatusCodes.Status403Forbidden, "session_forbidden")]
    [InlineData(typeof(InstantQuoteReplayConflictException), StatusCodes.Status409Conflict, "idempotency_conflict")]
    [InlineData(typeof(InstantQuoteUploadInProgressException), StatusCodes.Status409Conflict, "upload_in_progress")]
    [InlineData(typeof(InstantQuoteUnsafeContentException), StatusCodes.Status422UnprocessableEntity, "unsafe_content")]
    [InlineData(typeof(InstantQuotePayloadTooLargeException), StatusCodes.Status413PayloadTooLarge, "payload_too_large")]
    [InlineData(typeof(InstantQuoteUnsupportedMediaTypeException), StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type")]
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

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static HttpRequestMessage UploadRequestWithoutRequiredHeaders()
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "files", "part.stl");
        return new HttpRequestMessage(HttpMethod.Post, $"/file/v1/instant-quotation/sessions/{SessionId}/files")
        {
            Content = content,
        };
    }

    private static HttpRequestMessage FinalizeRequest(HttpContent? content)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/file/v1/instant-quotation/sessions/{SessionId}/finalizations")
        {
            Content = content,
        };
        request.Headers.Add("X-Quote-Session-Token", "token");
        request.Headers.Add("Idempotency-Key", "finalize-1");
        return request;
    }

    private static async Task<WebApplication> StartPipelineAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(InstantQuotationFilesController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, AllowAllPolicyProvider>();
        builder.Services.AddSingleton<IPolicyEvaluator, AllowAllPolicyEvaluator>();
        builder.Services.AddSingleton<IInstantQuoteFileService, StubService>();
        builder.Services.AddSingleton<IInstantQuoteMultipartReader, StubMultipartReader>();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static void AssertMethod(string name, string template)
    {
        var method = typeof(InstantQuotationFilesController).GetMethod(name)!;
        var route = Assert.Single(method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>());
        Assert.Equal(template, route.Template);
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
        public Guid? RemovedFileId { get; private set; }
        public InstantQuoteOwner? RemoveOwner { get; private set; }

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
                DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
                InstantQuoteFileContract.MaximumUploadBytes,
                InstantQuoteFileContract.SupportedExtensions));
        }

        public Task<InstantQuoteFileResponse> UploadAsync(
            Guid sessionId,
            InstantQuoteOwner owner,
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
            InstantQuoteOwner owner,
            string token,
            string idempotencyKey,
            FinalizeInstantQuoteFilesRequest request,
            CancellationToken cancellationToken)
        {
            FinalizeRequest = request;
            return Task.FromResult(new FinalizeInstantQuoteFilesResponse(request.QuotationRequestId, [
                new FinalizedInstantQuoteFileResponse(FileId, "private-bucket", "instant-quotation/final", "part.stl", "model/stl", 3, "ABC123", "finalized"),
            ]));
        }

        public Task RemoveAsync(Guid sessionId, InstantQuoteOwner owner, string token, Guid fileId,
            CancellationToken cancellationToken)
        {
            RemovedFileId = fileId;
            RemoveOwner = owner;
            return Task.CompletedTask;
        }
    }

    private sealed class AllowAllPolicyProvider : IAuthorizationPolicyProvider
    {
        private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            Task.FromResult<AuthorizationPolicy?>(Policy);
    }

    private sealed class AllowAllPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
        {
            var principal = Principal(new Claim(
                "sub",
                "pipeline-user",
                ClaimValueTypes.String,
                "https://issuer.example"));
            var ticket = new AuthenticationTicket(principal, "test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) => Task.FromResult(PolicyAuthorizationResult.Success());
    }
}
