using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Legacy.Maliev.FileService.Api.Controllers;

/// <summary>Preserves the authenticated legacy /Uploads contract.</summary>
[ApiController]
[Route("[controller]")]
[Authorize]
public sealed class UploadsController(IFileService service, IdempotentUploadCoordinator idempotency) : ControllerBase
{
    /// <summary>Deletes an uploaded object.</summary>
    [HttpDelete]
    [RequirePermission(FilePermissions.Delete)]
    public async Task<ActionResult> DeleteUploadAsync([FromQuery] string? bucket, [FromQuery] string? objectName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(objectName))
        {
            return BadRequest("Bucket and object name is required");
        }

        try
        {
            return await service.DeleteAsync(bucket, objectName, cancellationToken)
                ? NoContent()
                : BadRequest("Could not delete uploaded file");
        }
        catch (FileUploadValidationException)
        {
            return BadRequest("Could not delete uploaded file");
        }
    }

    /// <summary>Moves an uploaded object.</summary>
    [HttpPut]
    [RequirePermission(FilePermissions.Update)]
    public async Task<ActionResult> MoveUploadAsync(
        [FromQuery] string? sourceBucket,
        [FromQuery] string? sourceObjectName,
        [FromQuery] string? destinationBucket,
        [FromQuery] string? destinationObjectName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sourceBucket) || string.IsNullOrEmpty(sourceObjectName) ||
            string.IsNullOrEmpty(destinationBucket) || string.IsNullOrEmpty(destinationObjectName))
        {
            return BadRequest("Bucket and object names are required");
        }

        try
        {
            return await service.MoveAsync(sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken)
                ? NoContent()
                : BadRequest("Could not move the uploaded file");
        }
        catch (FileUploadValidationException)
        {
            return BadRequest("Could not move the uploaded file");
        }
    }

    /// <summary>Uploads files through private quarantine and malware scanning.</summary>
    [HttpPost]
    [RequestSizeLimit(FileApplicationService.MaximumRequestBytes)]
    [RequirePermission(FilePermissions.Create)]
    public async Task<ActionResult<UploadResultResponse>> UploadAsync(
        [FromQuery] string? bucket,
        [FromForm] List<IFormFile>? files,
        [FromQuery] string? path,
        CancellationToken cancellationToken,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        if (!ValidateFiles(files))
        {
            return BadRequest(ModelState.Values.Select(value => value.Errors));
        }

        if (string.IsNullOrWhiteSpace(bucket))
        {
            ModelState.AddModelError(nameof(bucket), "Bucket is required");
            return BadRequest(ModelState.Values.Select(value => value.Errors));
        }

        try
        {
            var uploadFiles = files!.Select(file => (IUploadFile)new FormUploadFile(file)).ToArray();
            var principalId = User.FindFirst("client_id")?.Value
                ?? User.FindFirst("azp")?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(idempotencyKey) && string.IsNullOrWhiteSpace(principalId))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, Problem("Upload identity is unavailable."));
            }
            var result = await idempotency.ExecuteAsync(
                principalId ?? string.Empty,
                idempotencyKey,
                bucket,
                path,
                uploadFiles,
                (generation, token) => service.UploadAsync(bucket, path, uploadFiles, generation, token),
                (generation, token) => service.ReconcileUploadAsync(bucket, path, uploadFiles, generation, token),
                cancellationToken);
            return Created("Google Cloud Storage", result);
        }
        catch (FileUploadValidationException exception)
        {
            ModelState.AddModelError(nameof(files), exception.Message);
            return BadRequest(ModelState.Values.Select(value => value.Errors));
        }
        catch (MalwareDetectedException exception)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Malware detected",
                Detail = exception.Message,
            });
        }
        catch (MalwareScannerUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Upload scanning unavailable",
                Detail = exception.Message,
            });
        }
        catch (UploadIdempotencyConflictException exception)
        {
            return Conflict(Problem(exception.Message, StatusCodes.Status409Conflict, "Upload replay conflict"));
        }
        catch (UploadIdempotencyInProgressException exception)
        {
            return Conflict(Problem(exception.Message, StatusCodes.Status409Conflict, "Upload in progress"));
        }
        catch (UploadOutcomeUnknownException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Problem(exception.Message, StatusCodes.Status503ServiceUnavailable, "Upload outcome unknown"));
        }
        catch (UploadIdempotencyUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Problem(exception.Message, StatusCodes.Status503ServiceUnavailable, "Upload replay protection unavailable"));
        }
    }

    private static ProblemDetails Problem(string detail, int status = StatusCodes.Status503ServiceUnavailable, string title = "Upload unavailable") =>
        new() { Status = status, Title = title, Detail = detail };

    private bool ValidateFiles(IReadOnlyCollection<IFormFile>? files)
    {
        if (files is null || files.Count == 0)
        {
            ModelState.AddModelError(nameof(files), "Files must not be empty");
            return false;
        }

        if (files.Any(file => string.IsNullOrEmpty(file.FileName)))
        {
            ModelState.AddModelError(nameof(files), "File must have file name");
            return false;
        }

        if (files.Any(file => file.Length <= 0))
        {
            ModelState.AddModelError(nameof(files), "File have length");
            return false;
        }

        if (files.Sum(file => file.Length) > FileApplicationService.MaximumUploadBytes)
        {
            ModelState.AddModelError(nameof(files), "File is too large");
            return false;
        }

        return true;
    }

    private sealed class FormUploadFile(IFormFile file) : IUploadFile
    {
        public string FileName => file.FileName;
        public string ContentType => file.ContentType;
        public long Length => file.Length;
        public Stream OpenReadStream() => file.OpenReadStream();
    }
}
