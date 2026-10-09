using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.FileService.Api.Controllers;
/// <summary>Returns authorized evidence for an exact immutable customer document version.</summary>
[ApiController]
[Authorize]
[Route("customers/{customerId:int}/documents")]
public sealed class CustomerDocumentsController(ICustomerDocumentRegistry? registry = null,
    IDocumentEvidenceVerificationService? verification = null) : ControllerBase
{
    /// <summary>Appends a current staff decision for an exact non-NDA evidence revision.</summary>
    [HttpPost("{documentId:guid}/versions/{versionId:guid}/verification")]
    [RequirePermission(CustomerDocumentPermissions.Verify, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<DocumentEvidenceReceipt>> Verify(int customerId, Guid documentId, Guid versionId,
        [FromBody] DocumentEvidenceVerificationRequest request, CancellationToken token)
    {
        if (customerId <= 0 || documentId == Guid.Empty || versionId == Guid.Empty || request.ExpectedVerificationRevision <= 0 ||
            request.Status is not ("Verified" or "Rejected") || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1024)
            return BadRequest();
        Response.Headers.CacheControl = "no-store";
        if (verification is null) return StatusCode(503);
        try
        {
            var receipt = await verification.VerifyAsync(new(User), customerId, documentId, versionId, request, token);
            return receipt is null ? NotFound() : Ok(receipt);
        }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
        catch (DocumentVerificationConflictException) { return Conflict(); }
        catch (DocumentVerificationKindException) { return UnprocessableEntity(); }
    }
    /// <summary>Reads bounded authorized document metadata backed by clean sealed versions.</summary>
    [HttpGet]
    [RequirePermission(CustomerDocumentPermissions.Read, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<IReadOnlyList<DocumentSummary>>> List(int customerId, CancellationToken token,
        [FromQuery] int offset = 0, [FromQuery] int limit = 20)
    {
        if (customerId <= 0 || offset is < 0 or > 10000 || limit is < 1 or > 50) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        if (registry is null) return StatusCode(503);
        try { return Ok(await registry.ListAsync(new(User), customerId, offset, limit, token)); }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
    }

    /// <summary>Reads bounded history for authorized clean exact versions.</summary>
    [HttpGet("{documentId:guid}/versions")]
    [RequirePermission(CustomerDocumentPermissions.Read, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<IReadOnlyList<DocumentVersionSummary>>> Versions(int customerId, Guid documentId, CancellationToken token,
        [FromQuery] int offset = 0, [FromQuery] int limit = 20)
    {
        if (customerId <= 0 || documentId == Guid.Empty || offset is < 0 or > 10000 || limit is < 1 or > 50) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        if (registry is null) return StatusCode(503);
        try
        {
            var versions = await registry.ReadVersionsAsync(new(User), customerId, documentId, offset, limit, token);
            return versions is null ? NotFound() : Ok(versions);
        }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
    }
    /// <summary>Reads the exact-version receipt after current customer authority checks.</summary>
    [HttpGet("{documentId:guid}/versions/{versionId:guid}/receipt")]
    [RequirePermission(CustomerDocumentPermissions.Read, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<ActionResult<DocumentEvidenceReceipt>> Receipt(int customerId, Guid documentId, Guid versionId, CancellationToken token)
    {
        if (customerId <= 0 || documentId == Guid.Empty || versionId == Guid.Empty) return BadRequest();
        if (registry is null) return StatusCode(503);
        try
        {
            var receipt = await registry.ReadReceiptAsync(new(User), customerId, documentId, versionId, token);
            Response.Headers.CacheControl = "no-store";
            return receipt is null ? NotFound() : Ok(receipt);
        }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
    }
}
