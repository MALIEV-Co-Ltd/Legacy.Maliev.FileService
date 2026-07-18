using System.Text.Json.Nodes;
using Legacy.Maliev.FileService.Api.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Legacy.Maliev.FileService.Api.OpenApi;

/// <summary>Publishes the exact generated OpenAPI contract consumed by the Web BFF.</summary>
public sealed class InstantQuoteOpenApiTransformer : IOpenApiOperationTransformer
{
    private const string JsonMediaType = "application/json";
    private const string ProblemMediaType = "application/problem+json";

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        context.Description.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        if (!string.Equals(controller, "InstantQuotationFiles", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        operation.Responses ??= new OpenApiResponses();
        AddAuthenticationResponses(operation);
        var action = (context.Description.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo.Name;
        switch (action)
        {
            case nameof(Controllers.InstantQuotationFilesController.CreateSessionAsync):
                ConfigureCreateSession(operation);
                break;
            case nameof(Controllers.InstantQuotationFilesController.UploadAsync):
                ConfigureUpload(operation);
                break;
            case nameof(Controllers.InstantQuotationFilesController.FinalizeAsync):
                ConfigureFinalize(operation);
                break;
            case nameof(Controllers.InstantQuotationFilesController.RemoveAsync):
                ConfigureRemove(operation);
                break;
        }

        return Task.CompletedTask;
    }

    private static void ConfigureCreateSession(OpenApiOperation operation)
    {
        operation.Responses!["201"] = JsonResponse(
            "Upload session created",
            SessionSchema(),
            """
            {
              "sessionId":"11111111-1111-1111-1111-111111111111",
              "sessionToken":"opaque-session-capability-32-chars",
              "expiresAt":"2026-07-18T12:00:00Z",
              "maxUploadBytes":209715200,
              "supportedExtensions":[".stl",".obj",".3mf",".step",".stp",".iges",".igs",".glb",".gltf"]
            }
            """);
        AddProblemResponse(operation, "503", "dependency_unavailable");
    }

    private static void ConfigureUpload(OpenApiOperation operation)
    {
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Exactly one streamed CAD file part named files; actual file bytes may not exceed 209715200 bytes.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "files" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["files"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "binary",
                                Description = "One CAD file with a safe filename and a supported declared media type.",
                            },
                        },
                    },
                },
            },
        };
        AddHeader(operation, "X-Quote-Session-Token", "Opaque upload-session capability.", 32, 512);
        AddHeader(operation, "Idempotency-Key", "Stable key for identical upload retries.", 16, 128);
        AddHeader(operation, "X-Content-SHA256", "SHA-256 digest of the exact file bytes.", pattern: "^[0-9A-Fa-f]{64}$");
        operation.Responses!["201"] = JsonResponse(
            "Clean file accepted",
            FileSchema(),
            """
            {"fileId":"22222222-2222-2222-2222-222222222222","fileName":"customer-part.stl","contentType":"model/stl","sizeBytes":123456,"sha256":"8f0d000000000000000000000000000000000000000000000000000000000000","status":"clean"}
            """);
        AddProblemResponse(operation, "400", "validation_error");
        AddProblemResponse(operation, "403", "session_forbidden", append: true);
        AddProblemResponse(operation, "409", "idempotency_conflict", "upload_in_progress");
        AddProblemResponse(operation, "413", "payload_too_large");
        AddProblemResponse(operation, "415", "unsupported_media_type");
        AddProblemResponse(operation, "422", "unsafe_content");
        AddProblemResponse(operation, "503", "dependency_unavailable", "outcome_unknown");
    }

    private static void ConfigureFinalize(OpenApiOperation operation)
    {
        AddHeader(operation, "X-Quote-Session-Token", "Opaque upload-session capability.", 32, 512);
        AddHeader(operation, "Idempotency-Key", "Stable key for identical finalization retries.", 16, 128);
        if (operation.RequestBody?.Content?.TryGetValue(JsonMediaType, out var requestMedia) == true)
        {
            requestMedia.Example = JsonNode.Parse(
                """
                {"quotationRequestId":"33333333-3333-3333-3333-333333333333","fileIds":["22222222-2222-2222-2222-222222222222"]}
                """);
        }
        operation.Responses!["200"] = JsonResponse(
            "Files finalized and linked",
            FinalizationSchema(),
            """
            {"quotationRequestId":"33333333-3333-3333-3333-333333333333","files":[{"fileId":"22222222-2222-2222-2222-222222222222","bucket":"private-upload-bucket","objectName":"instant-quotation/33333333333333333333333333333333/22222222222222222222222222222222.stl","fileName":"customer-part.stl","contentType":"model/stl","sizeBytes":123456,"sha256":"8f0d000000000000000000000000000000000000000000000000000000000000","status":"finalized"}]}
            """);
        AddProblemResponse(operation, "400", "validation_error");
        AddProblemResponse(operation, "403", "session_forbidden", append: true);
        AddProblemResponse(operation, "409", "idempotency_conflict", "upload_in_progress");
        AddProblemResponse(operation, "503", "dependency_unavailable", "outcome_unknown");
    }

    private static void ConfigureRemove(OpenApiOperation operation)
    {
        AddHeader(operation, "X-Quote-Session-Token", "Opaque upload-session capability.", 32, 512);
        operation.Responses!["204"] = new OpenApiResponse { Description = "File removed or already removed" };
        AddProblemResponse(operation, "400", "validation_error");
        AddProblemResponse(operation, "403", "session_forbidden", append: true);
        AddProblemResponse(operation, "409", "upload_in_progress");
        AddProblemResponse(operation, "503", "dependency_unavailable");
    }

    private static void AddAuthenticationResponses(OpenApiOperation operation)
    {
        AddProblemResponse(operation, "401", "platform_authentication_required");
        AddProblemResponse(operation, "403", "permission_forbidden");
    }

    private static void AddHeader(
        OpenApiOperation operation,
        string name,
        string description,
        int? minimumLength = null,
        int? maximumLength = null,
        string? pattern = null)
    {
        operation.Parameters ??= [];
        var existing = operation.Parameters.FirstOrDefault(parameter =>
            parameter.In == ParameterLocation.Header && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            operation.Parameters.Remove(existing);
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = minimumLength,
                MaxLength = maximumLength,
                Pattern = pattern,
            },
        });
    }

    private static OpenApiResponse JsonResponse(string description, IOpenApiSchema schema, string example) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            [JsonMediaType] = new()
            {
                Schema = schema,
                Example = JsonNode.Parse(example),
            },
        },
    };

    private static void AddProblemResponse(
        OpenApiOperation operation,
        string status,
        string firstCode,
        string? secondCode = null,
        bool append = false)
    {
        OpenApiMediaType media;
        if (append && operation.Responses!.TryGetValue(status, out var existing) &&
            existing.Content is not null && existing.Content.TryGetValue(ProblemMediaType, out var existingMedia))
        {
            media = existingMedia;
        }
        else
        {
            media = new OpenApiMediaType
            {
                Schema = ProblemSchema(),
                Examples = new Dictionary<string, IOpenApiExample>(),
            };
            operation.Responses![status] = new OpenApiResponse
            {
                Description = "Problem Details",
                Content = new Dictionary<string, OpenApiMediaType> { [ProblemMediaType] = media },
            };
        }

        AddProblemExample(media, firstCode);
        if (secondCode is not null)
        {
            AddProblemExample(media, secondCode);
        }
    }

    private static void AddProblemExample(OpenApiMediaType media, string code)
    {
        var definition = InstantQuoteProblem.FromCode(code);
        media.Examples![code] = new OpenApiExample
        {
            Summary = definition.Title,
            Value = new JsonObject
            {
                ["type"] = $"https://docs.maliev.com/problems/{definition.Code}",
                ["title"] = definition.Title,
                ["status"] = definition.Status,
                ["detail"] = definition.Detail,
                ["code"] = definition.Code,
            },
        };
    }

    private static OpenApiSchema ProblemSchema() => ObjectSchema(
        new HashSet<string> { "type", "title", "status", "detail", "code" },
        ("type", StringSchema("uri")),
        ("title", StringSchema()),
        ("status", IntegerSchema()),
        ("detail", StringSchema()),
        ("code", StringSchema()));

    private static OpenApiSchema SessionSchema() => ObjectSchema(
        new HashSet<string> { "sessionId", "sessionToken", "expiresAt", "maxUploadBytes", "supportedExtensions" },
        ("sessionId", StringSchema("uuid")),
        ("sessionToken", StringSchema()),
        ("expiresAt", StringSchema("date-time")),
        ("maxUploadBytes", IntegerSchema("int64")),
        ("supportedExtensions", new OpenApiSchema { Type = JsonSchemaType.Array, Items = StringSchema() }));

    private static OpenApiSchema FileSchema() => ObjectSchema(
        new HashSet<string> { "fileId", "fileName", "contentType", "sizeBytes", "sha256", "status" },
        ("fileId", StringSchema("uuid")),
        ("fileName", StringSchema()),
        ("contentType", StringSchema()),
        ("sizeBytes", IntegerSchema("int64")),
        ("sha256", StringSchema()),
        ("status", StringSchema()));

    private static OpenApiSchema FinalizationSchema() => ObjectSchema(
        new HashSet<string> { "quotationRequestId", "files" },
        ("quotationRequestId", StringSchema("uuid")),
        ("files", new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Items = ObjectSchema(
                new HashSet<string> { "fileId", "bucket", "objectName", "fileName", "contentType", "sizeBytes", "sha256", "status" },
                ("fileId", StringSchema("uuid")),
                ("bucket", StringSchema()),
                ("objectName", StringSchema()),
                ("fileName", StringSchema()),
                ("contentType", StringSchema()),
                ("sizeBytes", IntegerSchema("int64")),
                ("sha256", StringSchema()),
                ("status", StringSchema())),
        }));

    private static OpenApiSchema ObjectSchema(ISet<string> required, params (string Name, OpenApiSchema Schema)[] properties) => new()
    {
        Type = JsonSchemaType.Object,
        Required = required,
        Properties = properties.ToDictionary(
            property => property.Name,
            property => (IOpenApiSchema)property.Schema,
            StringComparer.Ordinal),
    };

    private static OpenApiSchema StringSchema(string? format = null) => new()
    {
        Type = JsonSchemaType.String,
        Format = format,
    };

    private static OpenApiSchema IntegerSchema(string? format = null) => new()
    {
        Type = JsonSchemaType.Integer,
        Format = format,
    };
}
