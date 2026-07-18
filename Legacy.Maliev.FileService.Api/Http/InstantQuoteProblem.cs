using System.Text.Json;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Legacy.Maliev.FileService.Api.Http;

/// <summary>Marks endpoints that publish the instant-quotation ProblemDetails contract.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InstantQuoteProblemContractAttribute : Attribute;

/// <summary>Reads exactly one streamed multipart file section for instant quotation.</summary>
public interface IInstantQuoteMultipartReader
{
    /// <summary>Reads exactly one part with the required field name without form buffering.</summary>
    Task<InstantQuoteMultipartFile> ReadSingleAsync(
        HttpRequest request,
        string requiredPartName,
        CancellationToken cancellationToken);
}

/// <summary>Owns the streamed multipart body and its safe request metadata.</summary>
public sealed class InstantQuoteMultipartFile(
    Stream body,
    InstantQuoteUploadMetadata metadata,
    Func<CancellationToken, Task>? complete = null) : IAsyncDisposable
{
    /// <summary>Gets the unbuffered file body.</summary>
    public Stream Body { get; } = body;

    /// <summary>Gets customer filename and declared media-type metadata.</summary>
    public InstantQuoteUploadMetadata Metadata { get; } = metadata;

    /// <summary>Checks that no multipart sections follow the consumed file section.</summary>
    public Task CompleteAsync(CancellationToken cancellationToken) =>
        complete?.Invoke(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Body.DisposeAsync();
}

/// <summary>Maps controller model-validation failures to the instant-quotation error contract.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InstantQuoteValidationProblemAttribute : ActionFilterAttribute
{
    /// <summary>Creates a filter that runs before the built-in ApiController model-state filter.</summary>
    public InstantQuoteValidationProblemAttribute()
    {
        Order = -4000;
    }

    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = InstantQuoteProblem.Create(
                new InstantQuoteValidationException("Request model validation failed."));
        }
    }
}

/// <summary>Creates safe RFC ProblemDetails responses for public contract failures.</summary>
public static class InstantQuoteProblem
{
    /// <summary>Maps a known workflow failure to its stable status and code.</summary>
    public static ObjectResult Create(InstantQuoteContractException exception)
    {
        var definition = FromException(exception);
        return new ObjectResult(CreateDetails(definition))
        {
            ContentTypes = { "application/problem+json" },
            StatusCode = definition.Status,
        };
    }

    /// <summary>Writes a stable authentication or permission failure.</summary>
    public static Task WriteAsync(HttpContext context, string code, CancellationToken cancellationToken = default)
    {
        var definition = FromCode(code);
        context.Response.StatusCode = definition.Status;
        context.Response.ContentType = "application/problem+json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            CreateDetails(definition),
            cancellationToken: cancellationToken);
    }

    internal static InstantQuoteProblemDefinition FromCode(string code) => code switch
    {
        "platform_authentication_required" => new(
            StatusCodes.Status401Unauthorized,
            code,
            "Platform authentication is required",
            "The caller must provide an accepted platform identity."),
        "permission_forbidden" => new(
            StatusCodes.Status403Forbidden,
            code,
            "File operation is not permitted",
            "The caller does not have permission to perform this file operation."),
        "validation_error" => FromException(new InstantQuoteValidationException(string.Empty)),
        "session_forbidden" => FromException(new InstantQuoteOwnershipException(string.Empty)),
        "idempotency_conflict" => FromException(new InstantQuoteReplayConflictException(string.Empty)),
        "upload_in_progress" => FromException(new InstantQuoteUploadInProgressException(string.Empty)),
        "unsafe_content" => FromException(new InstantQuoteUnsafeContentException(string.Empty)),
        "payload_too_large" => FromException(new InstantQuotePayloadTooLargeException(string.Empty)),
        "unsupported_media_type" => FromException(new InstantQuoteUnsupportedMediaTypeException(string.Empty)),
        "dependency_unavailable" => FromException(new InstantQuoteDependencyUnavailableException(string.Empty)),
        "outcome_unknown" => FromException(new InstantQuoteAmbiguousOutcomeException(string.Empty)),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    private static InstantQuoteProblemDefinition FromException(InstantQuoteContractException exception)
    {
        return exception switch
        {
            InstantQuoteValidationException => new(
                StatusCodes.Status400BadRequest,
                "validation_error",
                "Instant quotation request is invalid",
                "One or more request values are invalid."),
            InstantQuoteOwnershipException => new(
                StatusCodes.Status403Forbidden,
                "session_forbidden",
                "Upload session is not accessible",
                "The upload session could not be authorized."),
            InstantQuoteReplayConflictException => new(
                StatusCodes.Status409Conflict,
                "idempotency_conflict",
                "Idempotency replay conflict",
                "The idempotency key is already associated with a different request."),
            InstantQuoteUploadInProgressException => new(
                StatusCodes.Status409Conflict,
                "upload_in_progress",
                "Instant quotation operation is in progress",
                "Retry the identical request with the same idempotency key."),
            InstantQuoteUnsafeContentException => new(
                StatusCodes.Status422UnprocessableEntity,
                "unsafe_content",
                "Uploaded content is unsafe",
                "The uploaded content could not be accepted."),
            InstantQuotePayloadTooLargeException => new(
                StatusCodes.Status413PayloadTooLarge,
                "payload_too_large",
                "Upload is too large",
                $"The uploaded file exceeds {InstantQuoteFileContract.MaximumUploadBytes} bytes."),
            InstantQuoteUnsupportedMediaTypeException => new(
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type",
                "Upload media type is unsupported",
                "The declared media type or file extension is not supported."),
            InstantQuoteDependencyUnavailableException => new(
                StatusCodes.Status503ServiceUnavailable,
                "dependency_unavailable",
                "Instant quotation upload is unavailable",
                "A required upload dependency is temporarily unavailable."),
            InstantQuoteAmbiguousOutcomeException => new(
                StatusCodes.Status503ServiceUnavailable,
                "outcome_unknown",
                "Instant quotation outcome is unknown",
                "Retry the identical request with the same idempotency key."),
            _ => throw new ArgumentOutOfRangeException(nameof(exception)),
        };
    }

    private static ProblemDetails CreateDetails(InstantQuoteProblemDefinition definition)
    {
        var problem = new ProblemDetails
        {
            Status = definition.Status,
            Title = definition.Title,
            Detail = definition.Detail,
            Type = $"https://docs.maliev.com/problems/{definition.Code}",
        };
        problem.Extensions["code"] = definition.Code;
        return problem;
    }
}

internal sealed record InstantQuoteProblemDefinition(int Status, string Code, string Title, string Detail);

/// <summary>Produces stable authorization failures only for the instant-quotation contract.</summary>
public sealed class InstantQuoteAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<InstantQuoteProblemContractAttribute>() is null ||
            authorizeResult.Succeeded)
        {
            await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var code = authorizeResult.Challenged
            ? "platform_authentication_required"
            : "permission_forbidden";

        if (authorizeResult.Challenged)
        {
            if (policy.AuthenticationSchemes.Count > 0)
            {
                foreach (var scheme in policy.AuthenticationSchemes)
                {
                    await context.ChallengeAsync(scheme);
                }
            }
            else
            {
                await context.ChallengeAsync();
            }
        }
        else if (policy.AuthenticationSchemes.Count > 0)
        {
            foreach (var scheme in policy.AuthenticationSchemes)
            {
                await context.ForbidAsync(scheme);
            }
        }
        else
        {
            await context.ForbidAsync();
        }

        if (!context.Response.HasStarted)
        {
            await InstantQuoteProblem.WriteAsync(context, code, context.RequestAborted);
        }
    }
}
