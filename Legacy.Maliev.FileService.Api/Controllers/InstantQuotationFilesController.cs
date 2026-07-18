using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.FileService.Api.Controllers;

/// <summary>Publishes the Web-facing instant-quotation upload contract.</summary>
[ApiController]
[Route("file/v1/instant-quotation")]
[Authorize]
[InstantQuoteValidationProblem]
[InstantQuoteProblemContract]
public sealed class InstantQuotationFilesController(
    IInstantQuoteFileService service,
    IInstantQuoteMultipartReader multipartReader) : ControllerBase
{
    /// <summary>Creates an instant-quotation upload session.</summary>
    [HttpPost("sessions")]
    [RequirePermission(FilePermissions.Create)]
    [ProducesResponseType<CreateInstantQuoteSessionResponse>(StatusCodes.Status201Created)]
    public Task<ActionResult<CreateInstantQuoteSessionResponse>> CreateSessionAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            () => service.CreateInstantQuoteSessionAsync(ResolveOwner(), cancellationToken),
            response => Created($"/file/v1/instant-quotation/sessions/{response.SessionId}", response));
    }

    /// <summary>Uploads exactly one streamed multipart section named files.</summary>
    [HttpPost("sessions/{sessionId}/files")]
    [RequirePermission(FilePermissions.Create)]
    [DisableFormValueModelBinding]
    [ProducesResponseType<InstantQuoteFileResponse>(StatusCodes.Status201Created)]
    public Task<ActionResult<InstantQuoteFileResponse>> UploadAsync(
        [FromRoute] Guid sessionId,
        [FromHeader(Name = "X-Quote-Session-Token"), Required] string token,
        [FromHeader(Name = "Idempotency-Key"), Required] string idempotencyKey,
        [FromHeader(Name = "X-Content-SHA256"), Required] string expectedSha256,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            async () =>
            {
                var headers = InstantQuoteFilePolicy.NormalizeHeaders(token, idempotencyKey, expectedSha256);
                await using var file = await multipartReader.ReadSingleAsync(Request, "files", cancellationToken);
                var response = await service.UploadAsync(
                    sessionId,
                    ResolveOwner(),
                    headers.Token,
                    headers.IdempotencyKey,
                    headers.ExpectedSha256,
                    file.Body,
                    file.Metadata,
                    cancellationToken);
                await file.CompleteAsync(cancellationToken);
                return response;
            },
            response => Created($"/file/v1/instant-quotation/sessions/{sessionId}/files/{response.FileId}", response));
    }

    /// <summary>Finalizes selected clean files for a quotation request.</summary>
    [HttpPost("sessions/{sessionId}/finalizations")]
    [RequirePermission(FilePermissions.Create)]
    [ProducesResponseType<FinalizeInstantQuoteFilesResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult<FinalizeInstantQuoteFilesResponse>> FinalizeAsync(
        [FromRoute] Guid sessionId,
        [FromHeader(Name = "X-Quote-Session-Token"), Required] string token,
        [FromHeader(Name = "Idempotency-Key"), Required] string idempotencyKey,
        [FromBody, Required] FinalizeInstantQuoteFilesRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            () => service.FinalizeAsync(sessionId, ResolveOwner(), token, idempotencyKey, request, cancellationToken),
            response => Ok(response));
    }

    /// <summary>Idempotently removes one pre-finalization upload.</summary>
    [HttpDelete("sessions/{sessionId}/files/{fileId}")]
    [RequirePermission(FilePermissions.Create)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveAsync(
        [FromRoute] Guid sessionId,
        [FromRoute] Guid fileId,
        [FromHeader(Name = "X-Quote-Session-Token"), Required] string token,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.RemoveAsync(sessionId, ResolveOwner(), token, fileId, cancellationToken);
            return NoContent();
        }
        catch (InstantQuoteContractException exception)
        {
            return InstantQuoteProblem.Create(exception);
        }
    }

    private InstantQuoteOwner ResolveOwner()
    {
        var subject = User.FindFirst("sub")
            ?? User.FindFirst(ClaimTypes.NameIdentifier);
        var principalId = subject is null
            ? User.FindFirst("client_id")?.Value
                ?? User.FindFirst("azp")?.Value
            : $"{User.FindFirst("iss")?.Value ?? subject.Issuer}|{subject.Value}";
        return new InstantQuoteOwner(principalId, User.Identity?.IsAuthenticated == true);
    }

    private static async Task<ActionResult<T>> ExecuteAsync<T>(
        Func<Task<T>> operation,
        Func<T, ActionResult<T>> success)
    {
        try
        {
            return success(await operation());
        }
        catch (InstantQuoteContractException exception)
        {
            return InstantQuoteProblem.Create(exception);
        }
    }
}
