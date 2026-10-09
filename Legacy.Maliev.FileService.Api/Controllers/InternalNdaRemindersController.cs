using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.FileService.Api.Controllers;
/// <summary>Returns only the current responsible employee's durable in-app NDA worklist.</summary>
[ApiController]
[Authorize]
[Route("staff/nda-reminders")]
[TypeFilter(typeof(CustomerDocuments.NdaEndpointExceptionFilter))]
public sealed class InternalNdaRemindersController(NdaReminderScheduler? scheduler = null) : ControllerBase
{
    /// <summary>Reads a bounded worklist with active employee checks and no customer recipients.</summary>
    [HttpGet]
    [RequirePermission(CustomerDocumentPermissions.Read, ResourcePathTemplate = "/staff/nda-reminders")]
    public async Task<ActionResult<IReadOnlyList<InternalNdaReminderSummary>>> Read([FromQuery] int limit = 50,
        [FromQuery] DateTimeOffset? dueFromUtc = null, [FromQuery] DateTimeOffset? dueThroughUtc = null,
        [FromQuery] string? state = null, CancellationToken token = default)
    {
        if (scheduler is null) return StatusCode(503);
        InternalNdaReminderState? filterState = null;
        if (state is not null)
        {
            var name = Enum.GetNames<InternalNdaReminderState>().FirstOrDefault(x => x.Equals(state, StringComparison.OrdinalIgnoreCase));
            if (name is null) return BadRequest();
            filterState = Enum.Parse<InternalNdaReminderState>(name);
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(await scheduler.ReadWorklistAsync(new(User), limit, token, dueFromUtc, dueThroughUtc, filterState));
    }
}
