using System.Net;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.OpenApi;
using Legacy.Maliev.FileService.Application.Interfaces;
using Models = Legacy.Maliev.FileService.Application.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.OpenApi;

public sealed class InstantQuoteOpenApiContractTests
{
    [Fact]
    public async Task OpenApi_PublishesExactInstantQuoteWireContract()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var bearer = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        var paths = document.RootElement.GetProperty("paths");
        var session = paths.GetProperty("/file/v1/instant-quotation/sessions").GetProperty("post");
        AssertBearerSecurity(session);
        AssertResponse(
            session,
            "201",
            "application/json",
            "sessionId", "sessionToken", "expiresAt", "maxUploadBytes", "supportedExtensions");
        AssertProblem(session, "401", "platform_authentication_required");
        AssertProblem(session, "403", "permission_forbidden");
        AssertProblem(session, "503", "dependency_unavailable");

        var upload = paths.GetProperty("/file/v1/instant-quotation/sessions/{sessionId}/files").GetProperty("post");
        AssertBearerSecurity(upload);
        var multipart = upload.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data");
        Assert.True(upload.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.Contains("files", multipart.GetProperty("schema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var file = multipart.GetProperty("schema").GetProperty("properties").GetProperty("files");
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
        Assert.Equal(
            "model/stl, application/sla, application/vnd.ms-pki.stl, model/obj, text/plain, application/x-tgif, application/vnd.ms-package.3dmanufacturing-3dmodel+xml, model/step, application/step, model/iges, application/iges, model/gltf-binary, model/gltf+json, application/octet-stream",
            multipart.GetProperty("encoding").GetProperty("files").GetProperty("contentType").GetString());
        Assert.Equal(
            "solid example\nendsolid example\n",
            multipart.GetProperty("example").GetProperty("files").GetString());
        AssertHeader(upload, "X-Quote-Session-Token", 32, 512, null);
        AssertHeader(upload, "Idempotency-Key", 16, 128, null);
        AssertHeader(upload, "X-Content-SHA256", null, null, "^[0-9A-Fa-f]{64}$");
        AssertResponse(
            upload,
            "201",
            "application/json",
            "fileId", "fileName", "contentType", "sizeBytes", "sha256", "status");
        AssertProblem(upload, "400", "validation_error");
        AssertProblem(upload, "401", "platform_authentication_required");
        AssertProblem(upload, "403", "permission_forbidden", "session_forbidden");
        AssertProblem(upload, "409", "idempotency_conflict", "upload_in_progress");
        AssertProblem(upload, "413", "payload_too_large");
        AssertProblem(upload, "415", "unsupported_media_type");
        AssertProblem(upload, "422", "unsafe_content");
        AssertProblem(upload, "503", "dependency_unavailable", "outcome_unknown");

        var finalize = paths.GetProperty("/file/v1/instant-quotation/sessions/{sessionId}/finalizations").GetProperty("post");
        AssertBearerSecurity(finalize);
        AssertHeader(finalize, "X-Quote-Session-Token", 32, 512, null);
        AssertHeader(finalize, "Idempotency-Key", 16, 128, null);
        var finalizeRequest = finalize.GetProperty("requestBody").GetProperty("content").GetProperty("application/json");
        Assert.Equal("33333333-3333-3333-3333-333333333333", finalizeRequest.GetProperty("example").GetProperty("quotationRequestId").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", finalizeRequest.GetProperty("example").GetProperty("fileIds")[0].GetString());
        AssertResponse(finalize, "200", "application/json", "quotationRequestId", "files");
        var finalizedMedia = finalize.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json");
        var finalizedItemSchema = finalizedMedia.GetProperty("schema").GetProperty("properties").GetProperty("files").GetProperty("items");
        Assert.Equal(
            new[] { "bucket", "contentType", "fileId", "fileName", "objectName", "sha256", "sizeBytes", "status" },
            finalizedItemSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("private-upload-bucket", finalizedMedia.GetProperty("example").GetProperty("files")[0].GetProperty("bucket").GetString());
        AssertProblem(finalize, "400", "validation_error");
        AssertProblem(finalize, "401", "platform_authentication_required");
        AssertProblem(finalize, "403", "permission_forbidden", "session_forbidden");
        AssertProblem(finalize, "409", "idempotency_conflict", "upload_in_progress");
        AssertProblem(finalize, "503", "dependency_unavailable", "outcome_unknown");

        var remove = paths.GetProperty("/file/v1/instant-quotation/sessions/{sessionId}/files/{fileId}").GetProperty("delete");
        AssertBearerSecurity(remove);
        AssertHeader(remove, "X-Quote-Session-Token", 32, 512, null);
        Assert.True(remove.GetProperty("responses").TryGetProperty("204", out var noContent));
        Assert.False(noContent.TryGetProperty("content", out _));
        AssertProblem(remove, "400", "validation_error");
        AssertProblem(remove, "401", "platform_authentication_required");
        AssertProblem(remove, "403", "permission_forbidden", "session_forbidden");
        AssertProblem(remove, "409", "upload_in_progress");
        AssertProblem(remove, "503", "dependency_unavailable", "outcome_unknown");
    }

    private static void AssertBearerSecurity(JsonElement operation)
    {
        var requirement = Assert.Single(operation.GetProperty("security").EnumerateArray());
        Assert.True(requirement.TryGetProperty("Bearer", out var scopes));
        Assert.Empty(scopes.EnumerateArray());
    }

    private static void AssertHeader(JsonElement operation, string name, int? minimum, int? maximum, string? pattern)
    {
        var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), value =>
            value.GetProperty("in").GetString() == "header" && value.GetProperty("name").GetString() == name);
        Assert.True(parameter.GetProperty("required").GetBoolean());
        var schema = parameter.GetProperty("schema");
        if (minimum is not null) Assert.Equal(minimum, schema.GetProperty("minLength").GetInt32());
        if (maximum is not null) Assert.Equal(maximum, schema.GetProperty("maxLength").GetInt32());
        if (pattern is not null) Assert.Equal(pattern, schema.GetProperty("pattern").GetString());
    }

    private static void AssertResponse(JsonElement operation, string status, string mediaType, params string[] properties)
    {
        var content = operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty(mediaType);
        var expected = properties.Order().ToArray();
        Assert.Equal(expected, content.GetProperty("schema").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(expected, content.GetProperty("example").EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    private static void AssertProblem(JsonElement operation, string status, params string[] expectedCodes)
    {
        var media = operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/problem+json");
        var schema = media.GetProperty("schema");
        foreach (var property in new[] { "type", "title", "status", "detail", "code" })
        {
            Assert.True(schema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var actualCodes = media.GetProperty("examples").EnumerateObject()
            .Select(example => example.Value.GetProperty("value").GetProperty("code").GetString())
            .ToArray();
        foreach (var code in expectedCodes)
        {
            Assert.Contains(code, actualCodes);
        }
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(InstantQuotationFilesController).Assembly);
        builder.Services.AddSingleton<IInstantQuoteFileService, UnusedInstantQuoteFileService>();
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer<InstantQuoteOpenApiDocumentTransformer>();
            options.AddOperationTransformer<InstantQuoteOpenApiTransformer>();
        });
        var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();
        await app.StartAsync();
        return app;
    }

    private sealed class UnusedInstantQuoteFileService : IInstantQuoteFileService
    {
        public Task<Models.CreateInstantQuoteSessionResponse> CreateInstantQuoteSessionAsync(Models.InstantQuoteOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Models.InstantQuoteFileResponse> UploadAsync(Guid sessionId, Models.InstantQuoteOwner owner, string token, string idempotencyKey, string expectedSha256, Stream body, Models.InstantQuoteUploadMetadata metadata, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Models.FinalizeInstantQuoteFilesResponse> FinalizeAsync(Guid sessionId, Models.InstantQuoteOwner owner, string token, string idempotencyKey, Models.FinalizeInstantQuoteFilesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(Guid sessionId, Models.InstantQuoteOwner owner, string token, Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
