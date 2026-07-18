using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.FileService.Api.Controllers;

/// <summary>Publishes the Web-facing instant-quotation upload contract.</summary>
[ApiController]
[Route("file/v1/instant-quotation")]
[Authorize]
[InstantQuoteValidationProblem]
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
        var subject = User.FindFirst("sub")
            ?? User.FindFirst(ClaimTypes.NameIdentifier);
        var principalId = subject is null
            ? User.FindFirst("client_id")?.Value
                ?? User.FindFirst("azp")?.Value
            : $"{User.FindFirst("iss")?.Value ?? subject.Issuer}|{subject.Value}";
        var owner = new InstantQuoteOwner(principalId, User.Identity?.IsAuthenticated == true);

        return ExecuteAsync(
            () => service.CreateInstantQuoteSessionAsync(owner, cancellationToken),
            response => Created($"/file/v1/instant-quotation/sessions/{response.SessionId}", response));
    }

    /// <summary>Uploads exactly one streamed multipart section named files.</summary>
    [HttpPost("sessions/{sessionId}/files")]
    [RequirePermission(FilePermissions.Create)]
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
                await using var file = await multipartReader.ReadSingleAsync(Request, "files", cancellationToken);
                return await service.UploadAsync(
                    sessionId,
                    token,
                    idempotencyKey,
                    expectedSha256,
                    file.Body,
                    file.Metadata,
                    cancellationToken);
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
            () => service.FinalizeAsync(sessionId, token, idempotencyKey, request, cancellationToken),
            response => Ok(response));
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
