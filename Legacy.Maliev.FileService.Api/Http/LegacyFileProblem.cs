using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.FileService.Api.Http;

internal static class LegacyFileProblem
{
    internal static ObjectResult Unavailable() => new(new ProblemDetails
    {
        Status = StatusCodes.Status503ServiceUnavailable,
        Title = "Legacy file service unavailable",
        Detail = "File storage is temporarily unavailable.",
    })
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable,
    };
}
