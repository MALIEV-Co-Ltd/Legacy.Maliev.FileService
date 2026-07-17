using System.Text.Json.Serialization;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Maliev.Aspire.ServiceDefaults;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddPostgresDbContext<FileDbContext>(connectionName: "FileDbContext");
builder.AddStandardCache("legacy:file:");
builder.AddStandardCors();
builder.AddJwtAuthentication();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(
    title: "Legacy MALIEV File Service API",
    description: "Temporary .NET 10 compatibility service preserving secure legacy upload API contracts.");

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.DictionaryKeyPolicy = null;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetSection(FileStorageOptions.SectionName))
    .Validate(options => options.AllowedBuckets.Length > 0, "At least one allowed bucket is required")
    .Validate(options => options.SignedUrlHours is >= 1 and <= 168, "Signed URL lifetime must be between one hour and seven days")
    .ValidateOnStart();
builder.Services.AddOptions<MalwareScannerOptions>()
    .Bind(builder.Configuration.GetSection(MalwareScannerOptions.SectionName))
    .Validate(options => options.Port is > 0 and <= 65535, "Scanner port is invalid")
    .ValidateOnStart();
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = FileApplicationService.MaximumRequestBytes);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = FileApplicationService.MaximumRequestBytes);
builder.Services.AddSingleton(_ => StorageClient.Create());
builder.Services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<StorageClient>().CreateUrlSigner());
builder.Services.AddScoped<IObjectStorage, GoogleCloudObjectStorage>();
builder.Services.AddScoped<IFileSafetyScanner, ClamAvFileSafetyScanner>();
builder.Services.AddScoped<IUploadRepository, UploadRepository>();
builder.Services.AddScoped<IUploadIdempotencyStore, RedisUploadIdempotencyStore>();
builder.Services.AddScoped<ObjectNamePolicy>();
builder.Services.AddScoped<IFileService, FileApplicationService>();
builder.Services.AddScoped<IdempotentUploadCoordinator>();

var app = builder.Build();

app.UseStandardMiddleware();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints("file");
app.MapControllers();
app.MapApiDocumentation(servicePrefix: "file");

await app.RunAsync();

/// <summary>Legacy File Service entry point.</summary>
public partial class Program;
