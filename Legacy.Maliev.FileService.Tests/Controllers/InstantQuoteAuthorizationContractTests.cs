using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Models = Legacy.Maliev.FileService.Application.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.Controllers;

public sealed class InstantQuoteAuthorizationContractTests
{
    [Theory]
    [InlineData(false, false, HttpStatusCode.Unauthorized, "platform_authentication_required")]
    [InlineData(true, false, HttpStatusCode.Forbidden, "permission_forbidden")]
    public async Task AuthorizationFailures_ReturnStableProblemDetails(
        bool authenticated,
        bool authorized,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var app = await StartAsync(authenticated, authorized);
        using var response = await app.GetTestClient().PostAsync("/file/v1/instant-quotation/sessions", null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        if (!authenticated)
        {
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(value => value.Scheme));
        }

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = problem.RootElement;
        Assert.Equal(
            new[] { "code", "detail", "status", "title", "type" },
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal($"https://docs.maliev.com/problems/{expectedCode}", root.GetProperty("type").GetString());
        if (expectedCode == "platform_authentication_required")
        {
            Assert.Equal("Platform authentication is required", root.GetProperty("title").GetString());
            Assert.Equal("The caller must provide an accepted platform identity.", root.GetProperty("detail").GetString());
        }
        else
        {
            Assert.Equal("File operation is not permitted", root.GetProperty("title").GetString());
            Assert.Equal("The caller does not have permission to perform this file operation.", root.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task LegacyEndpoint_UsesDefaultAuthorizationResponse()
    {
        await using var app = await StartAsync(authenticated: false, authorized: false);
        using var response = await app.GetTestClient().GetAsync("/legacy-protected");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    private static async Task<WebApplication> StartAsync(bool authenticated, bool authorized)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiVersioning();
        builder.Services.AddControllers().AddApplicationPart(typeof(InstantQuotationFilesController).Assembly);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, ContractPolicyProvider>();
        builder.Services.AddSingleton<IPolicyEvaluator>(new ContractPolicyEvaluator(authenticated, authorized));
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, InstantQuoteAuthorizationResultHandler>();
        builder.Services.AddSingleton<IInstantQuoteFileService, UnusedInstantQuoteFileService>();
        builder.Services.AddSingleton<IInstantQuoteMultipartReader, UnusedMultipartReader>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/legacy-protected", [Authorize] () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    private sealed class ContractPolicyProvider : IAuthorizationPolicyProvider
    {
        private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(Policy);
    }

    private sealed class ContractPolicyEvaluator(bool authenticated, bool authorized) : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
        {
            if (!authenticated) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-user")], JwtBearerDefaults.AuthenticationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, JwtBearerDefaults.AuthenticationScheme)));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(AuthorizationPolicy policy, AuthenticateResult authenticationResult, HttpContext context, object? resource) =>
            Task.FromResult(authorized ? PolicyAuthorizationResult.Success() : authenticated ? PolicyAuthorizationResult.Forbid() : PolicyAuthorizationResult.Challenge());
    }

    private sealed class UnusedMultipartReader : IInstantQuoteMultipartReader
    {
        public Task<InstantQuoteMultipartFile> ReadSingleAsync(HttpRequest request, string requiredPartName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedInstantQuoteFileService : IInstantQuoteFileService
    {
        public Task<Models.CreateInstantQuoteSessionResponse> CreateInstantQuoteSessionAsync(Models.InstantQuoteOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Models.InstantQuoteFileResponse> UploadAsync(Guid sessionId, Models.InstantQuoteOwner owner, string token, string idempotencyKey, string expectedSha256, Stream body, Models.InstantQuoteUploadMetadata metadata, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Models.FinalizeInstantQuoteFilesResponse> FinalizeAsync(Guid sessionId, Models.InstantQuoteOwner owner, string token, string idempotencyKey, Models.FinalizeInstantQuoteFilesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(Guid sessionId, Models.InstantQuoteOwner owner, string token, Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
