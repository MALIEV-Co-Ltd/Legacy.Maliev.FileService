using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.FileService.Api.Controllers;

/// <summary>Preserves the authenticated legacy /uploads/SignedUrl contract.</summary>
[ApiController]
[Route("uploads/[controller]")]
[Authorize]
public sealed class SignedUrlController(IFileService service) : ControllerBase
{
    /// <summary>Creates a time-limited URL for a recorded clean object.</summary>
    [HttpGet]
    [RequirePermission(FilePermissions.Read)]
    public async Task<ActionResult<Uri>> GetSignedUrlAsync([FromQuery] string? bucket, [FromQuery] string? objectName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(objectName))
        {
            return BadRequest();
        }

        try
        {
            var uri = await service.GetSignedUrlAsync(bucket, objectName, cancellationToken);
            return uri is null ? NotFound() : uri;
        }
        catch (FileUploadValidationException)
        {
            return BadRequest();
        }
    }
}
