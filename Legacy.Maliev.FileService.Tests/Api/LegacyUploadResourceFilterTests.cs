using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Tests.Api;

public sealed class LegacyUploadResourceFilterTests
{
    [Fact]
    public async Task PostUploads_DisabledRuntime_ReturnsUnavailableBeforeModelBindingReadsRequestBody()
    {
        var body = new FailOnReadStream();
        var store = new RecordingIdempotencyStore();
        var service = new RecordingFileService();
        await using var app = await StartPipelineAsync(body, store, service);
        using var client = app.GetTestClient();
        using var content = new ByteArrayContent([1]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Uploads?bucket=maliev.com")
        {
            Content = content,
        };
        request.Headers.Add("Idempotency-Key", "workflow-42");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, body.ReadCalls);
        Assert.Equal(0, store.AcquireCalls);
        Assert.Equal(0, service.Calls);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Legacy file service unavailable", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void LegacyWriteGateAttribute_AppliesOnlyToLegacyPostUploadAction()
    {
        var upload = typeof(UploadsController).GetMethod(nameof(UploadsController.UploadAsync))!;
        var delete = typeof(UploadsController).GetMethod(nameof(UploadsController.DeleteUploadAsync))!;
        var move = typeof(UploadsController).GetMethod(nameof(UploadsController.MoveUploadAsync))!;
        var instantUpload = typeof(InstantQuotationFilesController).GetMethod(
            nameof(InstantQuotationFilesController.UploadAsync))!;

        Assert.NotNull(upload.GetCustomAttribute<RequireLegacyFileWritesAttribute>());
        Assert.Null(delete.GetCustomAttribute<RequireLegacyFileWritesAttribute>());
        Assert.Null(move.GetCustomAttribute<RequireLegacyFileWritesAttribute>());
        Assert.Null(instantUpload.GetCustomAttribute<RequireLegacyFileWritesAttribute>());
    }

    private static async Task<WebApplication> StartPipelineAsync(
        FailOnReadStream body,
        RecordingIdempotencyStore store,
        RecordingFileService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(UploadsController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, AllowAllPolicyProvider>();
        builder.Services.AddSingleton<IPolicyEvaluator, AllowAllPolicyEvaluator>();
        builder.Services.AddSingleton<IFileService>(service);
        builder.Services.AddSingleton<IUploadIdempotencyStore>(store);
        builder.Services.AddSingleton<IdempotentUploadCoordinator>();
        builder.Services.AddSingleton(new LegacyFileRuntimeGate(Options.Create(new FileStorageOptions
        {
            Enabled = false,
            WritesEnabled = true,
        })));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Request.Body = body;
            await next();
        });
        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private sealed class FailOnReadStream : Stream
    {
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ThrowRead();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(ReadException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ThrowRead() => throw ReadException();
        private InvalidOperationException ReadException()
        {
            ReadCalls++;
            return new InvalidOperationException("Disabled upload request body must not be read.");
        }
    }

    private sealed class RecordingIdempotencyStore : IUploadIdempotencyStore
    {
        public int AcquireCalls { get; private set; }
        public Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, string effectivePath, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            throw new InvalidOperationException("Disabled uploads must not touch replay persistence.");
        }
        public Task<bool> RenewAsync(string identity, string reservationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(string identity, string fingerprint, string reservationId, UploadResultResponse response, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkUnknownAsync(string identity, string reservationId, UploadResultResponse? response, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAsync(string identity, string reservationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingFileService : IFileService
    {
        public int Calls { get; private set; }
        public Task<UploadResultResponse> UploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, CancellationToken cancellationToken) => Called<UploadResultResponse>();
        public Task<UploadResultResponse> UploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, Guid operationId, CancellationToken cancellationToken) => Called<UploadResultResponse>();
        public Task<UploadResultResponse?> ReconcileUploadAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, Guid operationId, CancellationToken cancellationToken) => Called<UploadResultResponse?>();
        public Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken) => Called<bool>();
        public Task<bool> MoveAsync(string sourceBucket, string sourceObjectName, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) => Called<bool>();
        public Task<Uri?> GetSignedUrlAsync(string bucket, string objectName, CancellationToken cancellationToken) => Called<Uri?>();
        private Task<T> Called<T>()
        {
            Calls++;
            throw new InvalidOperationException("Disabled uploads must not invoke the file service.");
        }
    }

    private sealed class AllowAllPolicyProvider : IAuthorizationPolicyProvider
    {
        private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(Policy);
    }

    private sealed class AllowAllPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) => Task.FromResult(PolicyAuthorizationResult.Success());
    }
}
