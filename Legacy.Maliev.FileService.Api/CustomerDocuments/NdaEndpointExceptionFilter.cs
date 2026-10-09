using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Legacy.Maliev.FileService.Api.CustomerDocuments;
/// <summary>Maps domain and persistence failures to bounded responses without provider details.</summary>
public sealed class NdaEndpointExceptionFilter : IExceptionFilter
{
    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        var code = context.Exception switch
        {
            DocumentAuthorityUnavailableException or DbUpdateException or NpgsqlException => 503,
            DocumentAuthorityDeniedException => 403,
            NdaNotFoundException => 404,
            NdaRevisionConflictException => 409,
            NdaValidationException => 422,
            ArgumentException => 400,
            _ => 0,
        };
        if (code == 0) return;
        context.Result = new StatusCodeResult(code);
        context.ExceptionHandled = true;
    }
}
