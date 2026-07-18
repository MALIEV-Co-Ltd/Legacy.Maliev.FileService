using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Legacy.Maliev.FileService.Api.Http;

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
        var (status, code, title, detail) = exception switch
        {
            InstantQuoteValidationException => (
                StatusCodes.Status400BadRequest,
                "validation_error",
                "Instant quotation request is invalid",
                "One or more request values are invalid."),
            InstantQuoteOwnershipException => (
                StatusCodes.Status403Forbidden,
                "session_forbidden",
                "Upload session is not accessible",
                "The upload session could not be authorized."),
            InstantQuoteReplayConflictException => (
                StatusCodes.Status409Conflict,
                "idempotency_conflict",
                "Idempotency replay conflict",
                "The idempotency key is already associated with a different request."),
            InstantQuoteUploadInProgressException => (
                StatusCodes.Status409Conflict,
                "upload_in_progress",
                "Instant quotation operation is in progress",
                "Retry the identical request with the same idempotency key."),
            InstantQuoteUnsafeContentException => (
                StatusCodes.Status422UnprocessableEntity,
                "unsafe_content",
                "Uploaded content is unsafe",
                "The uploaded content could not be accepted."),
            InstantQuotePayloadTooLargeException => (
                StatusCodes.Status413PayloadTooLarge,
                "payload_too_large",
                "Upload is too large",
                $"The uploaded file exceeds {InstantQuoteFileContract.MaximumUploadBytes} bytes."),
            InstantQuoteDependencyUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "dependency_unavailable",
                "Instant quotation upload is unavailable",
                "A required upload dependency is temporarily unavailable."),
            InstantQuoteAmbiguousOutcomeException => (
                StatusCodes.Status503ServiceUnavailable,
                "outcome_unknown",
                "Instant quotation outcome is unknown",
                "Retry the identical request with the same idempotency key."),
            _ => throw new ArgumentOutOfRangeException(nameof(exception)),
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://docs.maliev.com/problems/{code}",
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" },
            StatusCode = status,
        };
    }
}
