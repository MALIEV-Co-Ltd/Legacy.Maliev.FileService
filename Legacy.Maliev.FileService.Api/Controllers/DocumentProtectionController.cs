using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.FileService.Api.Controllers;
/// <summary>Provides current server-side protection evaluation for scoped resources and derivatives.</summary>
[ApiController]
[Authorize]
[Route("customers/{customerId:int}/protection")]
[TypeFilter(typeof(CustomerDocuments.NdaEndpointExceptionFilter))]
public sealed class DocumentProtectionController(IDocumentProtectionService? protection = null) : ControllerBase
{
    /// <summary>Evaluates protection independently of existing social-publication consent.</summary>
    [HttpPost("evaluate")]
    [RequirePermission(CustomerDocumentPermissions.EvaluateProtection, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<ProtectionDecision>> Evaluate(int customerId, ProtectionRequest request, CancellationToken token)
    {
        if (protection is null) return StatusCode(503);
        Response.Headers.CacheControl = "no-store";
        return Ok(await protection.EvaluateAsync(new(User), customerId, request, token));
    }
}
