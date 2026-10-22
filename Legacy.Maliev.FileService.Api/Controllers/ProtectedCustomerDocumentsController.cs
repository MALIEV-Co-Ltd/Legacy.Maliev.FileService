using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.FileService.Api.Controllers;
/// <summary>Provides authenticated attachment and protected immutable upload endpoints.</summary>
[ApiController, Authorize, Route("customers/{customerId:int}/documents")]
public sealed class ProtectedCustomerDocumentsController(CustomerDocumentUploadService? uploads = null, CustomerDocumentDownloadService? downloads = null) : ControllerBase
{
    /// <summary>Creates one clean document using a durable client operation key.</summary>
    [HttpPost, RequestSizeLimit(21 * 1024 * 1024)]
    [RequirePermission(CustomerDocumentPermissions.Write, ResourcePathTemplate = "/customers/{customerId}")]
    public Task<IActionResult> Upload(int customerId, [FromForm] ProtectedDocumentUploadForm form, CancellationToken token) => UploadVersion(customerId, null, form, token);
    /// <summary>Creates a replacement immutable version.</summary>
    [HttpPost("{documentId:guid}/versions"), RequestSizeLimit(21 * 1024 * 1024)]
    [RequirePermission(CustomerDocumentPermissions.Write, ResourcePathTemplate = "/customers/{customerId}")]
    public Task<IActionResult> Version(int customerId, Guid documentId, [FromForm] ProtectedDocumentUploadForm form, CancellationToken token) => UploadVersion(customerId, documentId, form, token);
    private async Task<IActionResult> UploadVersion(int customerId, Guid? documentId, ProtectedDocumentUploadForm form, CancellationToken token)
    {
        if (uploads is null) return StatusCode(503);
        if (form.Files.Count != 1 || !Request.Headers.TryGetValue("Idempotency-Key", out var key) || key.Count != 1) return BadRequest();
        try
        {
            var associationJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            associationJson.Converters.Add(new JsonStringEnumConverter<DocumentResourceKind>(allowIntegerValues: false));
            var associations = JsonSerializer.Deserialize<DocumentAssociation[]>(form.Associations, associationJson) ?? [];
            var receipt = await uploads.UploadAsync(new(User), customerId, new(documentId, form.Kind, form.Title, form.Visibility, associations, form.ExpectedRevision), new FormUpload(form.Files[0]), key.ToString(), token);
            Response.Headers.CacheControl = "no-store";
            return Ok(receipt);
        }
        catch (JsonException) { return BadRequest(); }
        catch (DocumentContentException exception) { return StatusCode(exception.StatusCode); }
        catch (DocumentConflictException) { return Conflict(); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
    }
    /// <summary>Transfers only fully validated private bytes as a bounded attachment.</summary>
    [HttpGet("{documentId:guid}/versions/{versionId:guid}/download")]
    [RequirePermission(CustomerDocumentPermissions.Read, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<IActionResult> Download(int customerId, Guid documentId, Guid versionId, CancellationToken token)
    {
        if (downloads is null) return StatusCode(503);
        try
        {
            var result = await downloads.ReadAsync(new(User), customerId, documentId, versionId, token);
            if (result is null) return NotFound();
            Response.Headers.CacheControl = "no-store";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(result.Bytes.ToArray(), result.ContentType, result.FileName, enableRangeProcessing: false);
        }
        catch (DocumentContentException exception) { return StatusCode(exception.StatusCode); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
    }
    /// <summary>Retains originals while archiving with reason, concurrency and hold checks.</summary>
    [HttpPost("{documentId:guid}/archive")]
    [RequirePermission(CustomerDocumentPermissions.Archive, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<IActionResult> Archive(int customerId, Guid documentId, [FromBody] ProtectedDocumentArchiveRequest request, CancellationToken token)
    {
        if (downloads is null) return StatusCode(503);
        try { await downloads.ArchiveAsync(new(User), customerId, documentId, request.ExpectedRevision, request.Reason, token); return NoContent(); }
        catch (DocumentContentException exception) { return StatusCode(exception.StatusCode); }
        catch (DocumentConflictException) { return Conflict(); }
        catch (DocumentAuthorityDeniedException) { return Forbid(); }
        catch (DocumentAuthorityUnavailableException) { return StatusCode(503); }
    }
    private sealed class FormUpload(IFormFile file) : IUploadFile
    {
        public string FileName => file.FileName;
        public string ContentType => file.ContentType;
        public long Length => file.Length;
        public Stream OpenReadStream() => file.OpenReadStream();
    }
}
/// <summary>Defines one bounded multipart upload form.</summary>
public sealed class ProtectedDocumentUploadForm
{
    /// <summary>Gets or sets the single legacy-compatible files field.</summary>
    public List<IFormFile> Files { get; set; } = [];
    /// <summary>Gets or sets the document category.</summary>
    public DocumentKind Kind { get; set; }
    /// <summary>Gets or sets the immutable title.</summary>
    public string Title { get; set; } = "";
    /// <summary>Gets or sets immutable visibility.</summary>
    public DocumentVisibility Visibility { get; set; }
    /// <summary>Gets or sets canonical association requests as JSON.</summary>
    public string Associations { get; set; } = "[]";
    /// <summary>Gets or sets expected document revision for replacement.</summary>
    public long? ExpectedRevision { get; set; }
}
/// <summary>Requires explicit archive revision and reason.</summary>
public sealed record ProtectedDocumentArchiveRequest(long ExpectedRevision, string Reason);
