using System.Text.Json.Serialization;
using Legacy.Maliev.FileService.Api;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Api.OpenApi;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Maliev.Aspire.ServiceDefaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddPostgresDbContext<FileDbContext>(connectionName: "FileDbContext");
builder.AddStandardCache("legacy:file:");
builder.AddStandardCors();
builder.AddJwtAuthentication();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddFileHostTransportPolicy();
builder.AddStandardOpenApi(
    title: "Legacy MALIEV File Service API",
    description: "Temporary .NET 10 compatibility service preserving secure legacy upload API contracts.");
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<InstantQuoteOpenApiDocumentTransformer>();
    options.AddOperationTransformer<InstantQuoteOpenApiTransformer>();
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.DictionaryKeyPolicy = null;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, InstantQuoteAuthorizationResultHandler>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = FileApplicationService.MaximumRequestBytes;
    options.ValueCountLimit = 1024;
    options.ValueLengthLimit = 4 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = FileApplicationService.MaximumRequestBytes);
builder.AddHostedFinancialCompletionAcceptance();
builder.Services.AddFileServiceRuntime(builder.Configuration);

var app = builder.Build();

app.UseFileHostTransportBoundary();
app.UseStandardMiddleware();
app.UseFileHostTransportPolicy();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints("file");
app.MapControllers();
app.MapApiDocumentation(servicePrefix: "file");

if (app.Environment.IsEnvironment(HostedFinancialCompletionProfile.EnvironmentName))
{
    app.MapGet("/file/acceptance/signing-key", (HostedAcceptanceSigningIdentity identity, HostedAcceptanceDependencyLease lease) =>
        lease.IsCurrent ? (IResult)Results.Json(new { Algorithm = "GOOG4-RSA-SHA256", PublicKey = identity.VerificationPublicKey },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null }) : Results.StatusCode(503))
        .RequireAuthorization();
}

await app.RunAsync();

/// <summary>Legacy File Service entry point.</summary>
public partial class Program;
