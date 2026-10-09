using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.FileService.Api.Controllers;
/// <summary>Accepts employee review of an exact externally signed agreement version.</summary>
[ApiController]
[Authorize]
[Route("customers/{customerId:int}/documents/{documentId:guid}/nda")]
[TypeFilter(typeof(CustomerDocuments.NdaEndpointExceptionFilter))]
public sealed class NdaVerificationController(NdaVerificationService? verifier = null) : ControllerBase
{
    /// <summary>Appends staff verification; no release, override or signing action is exposed.</summary>
    [HttpPost("verification")]
    [RequirePermission(CustomerDocumentPermissions.Verify, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<NdaVerificationReceipt>> Verify(int customerId, Guid documentId, NdaVerificationRequest request, CancellationToken token)
    {
        if (verifier is null) return StatusCode(503);
        Response.Headers.CacheControl = "no-store";
        return Ok(await verifier.VerifyAsync(new(User), customerId, documentId, request, token));
    }
    /// <summary>Reads server-computed employee lifecycle badges for the current or selected exact agreement.</summary>
    [HttpGet]
    [RequirePermission(CustomerDocumentPermissions.Read, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<NdaAgreementSummary>> Read(int customerId, Guid documentId, [FromQuery] Guid? versionId, CancellationToken token)
    {
        if (verifier is null) return StatusCode(503);
        Response.Headers.CacheControl = "no-store";
        return Ok(await verifier.ReadAsync(new(User), customerId, documentId, versionId, token));
    }
}
