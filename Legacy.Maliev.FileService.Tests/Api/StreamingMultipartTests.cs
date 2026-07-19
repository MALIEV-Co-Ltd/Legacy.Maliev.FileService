using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using Asp.Versioning;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Api.Http;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.FileService.Tests.Api;

public sealed class StreamingMultipartTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Reader_ValidRawMultipart_ReturnsFileMetadata()
    {
        var body = "--boundary\r\nContent-Disposition: form-data; name=\"files\"; filename=\"part.stl\"\r\nContent-Type: model/stl\r\n\r\nabc\r\n--boundary--\r\n";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=boundary";
        context.Request.Body = RawBody(body);

        await using var file = await new SingleFileMultipartReader().ReadSingleAsync(context.Request, "files", default);

        Assert.Equal("part.stl", file.Metadata.FileName);
    }

    [Fact]
    public async Task Pipeline_ChunkedNonSeekableSingleFilesPart_StreamsAndReturnsCreated()
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        using var request = UploadRequest(RawMultipart("files", "part.STL", "model/stl", "abc"));

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected Created but received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}; " +
            $"reader={app.Services.GetRequiredService<RecordingMultipartReader>().ExceptionMessage}");
        var service = app.Services.GetRequiredService<ConsumingService>();
        Assert.Equal(3, service.BytesRead);
        Assert.Equal(new string('a', 64), service.ExpectedSha256);
        Assert.Equal("part.STL", service.Metadata?.FileName);
        var reader = app.Services.GetRequiredService<RecordingMultipartReader>();
        Assert.False(reader.RequestBodyCanSeek);
        Assert.Null(reader.RequestContentLength);
    }

    [Theory]
    [InlineData("multipart/form-data", "--boundary--\r\n")]
    [InlineData("multipart/form-data; boundary=boundary", "--boundary--\r\n")]
    [InlineData("multipart/form-data; boundary=boundary", "--boundary\r\nContent-Disposition: form-data; name=wrong; filename=part.stl\r\nContent-Type: model/stl\r\n\r\na\r\n--boundary--\r\n")]
    public async Task Pipeline_MissingBoundaryZeroOrWrongPart_ReturnsStableValidationError(
        string contentType,
        string body)
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        using var request = UploadRequest(new StringContent(body));
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Pipeline_AdditionalPartAfterConsumedFile_ReturnsStableValidationError()
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        using var request = UploadRequest(Multipart(
            FilePart("files", "part.stl", "model/stl", new GeneratedStream(3)),
            new StringContent("unexpected") { Headers = { ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"extra\"" } } }));

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.False(app.Services.GetRequiredService<ConsumingService>().UploadReturned);
    }

    [Fact]
    public async Task Pipeline_ActualBytesAboveLimit_ReturnsStablePayloadTooLarge()
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        using var content = new StreamContent(new MultipartPayloadStream(InstantQuoteFileContract.MaximumUploadBytes + 1));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        using var request = UploadRequest(content);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    [Fact]
    public async Task Pipeline_TruncatedFirstFilePart_ReturnsStableValidationError()
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        using var content = RawContent(
            "--boundary\r\nContent-Disposition: form-data; name=\"files\"; filename=\"part.stl\"\r\nContent-Type: model/stl\r\n\r\nabc");
        using var request = UploadRequest(content);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Pipeline_MalformedTrailingSection_ReturnsStableValidationError()
    {
        await using var app = await StartPipelineAsync();
        using var client = app.GetTestClient();
        using var content = RawContent(
            "--boundary\r\nContent-Disposition: form-data; name=\"files\"; filename=\"part.stl\"\r\nContent-Type: model/stl\r\n\r\nabc\r\n--boundary\r\nContent-Disposition:");
        using var request = UploadRequest(content);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Pipeline_FiniteChunkedRequestCancellation_AbortsReaderAndService()
    {
        using var coordinator = new CancellationCoordinator();
        await using var app = await StartPipelineAsync(coordinator);
        using var client = app.GetTestClient();
        var service = app.Services.GetRequiredService<ConsumingService>();
        using var request = UploadRequest(RawMultipart("files", "part.stl", "model/stl", "abc"));
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await coordinator.ServiceStarted.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.CancelRequest();
        try
        {
            await service.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            coordinator.Release();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(service.ObservedCancellationToken.IsCancellationRequested);
        Assert.True(app.Services.GetRequiredService<RecordingMultipartReader>().ObservedCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Controller_RequestAborted_PropagatesCancellationToReaderAndService()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ConsumingService(throwCancellation: true);
        var reader = new FiniteMultipartReader();
        var context = new DefaultHttpContext();
        context.RequestAborted = cancellation.Token;
        var controller = new InstantQuotationFilesController(service, reader)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = context,
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.UploadAsync(
                SessionId,
                new string('t', 32),
                new string('i', 16),
                new string('a', 64),
                context.RequestAborted));

        Assert.Equal(context.RequestAborted, reader.ObservedCancellationToken);
        Assert.Equal(context.RequestAborted, service.ObservedCancellationToken);
    }

    [Fact]
    public async Task Controller_ServiceIOException_IsNotTranslatedAsMultipartValidation()
    {
        var expected = new IOException("storage failed");
        var controller = new InstantQuotationFilesController(
            new ConsumingService(throwCancellation: false, uploadException: expected),
            new FiniteMultipartReader())
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var actual = await Assert.ThrowsAsync<IOException>(() => controller.UploadAsync(
            SessionId,
            new string('t', 32),
            new string('i', 16),
            new string('a', 64),
            default));

        Assert.Same(expected, actual);
    }

    private static HttpRequestMessage UploadRequest(HttpContent content)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/file/v1/instant-quotation/sessions/{SessionId}/files")
        {
            Content = content,
        };
        request.Headers.Add("X-Quote-Session-Token", new string('t', 32));
        request.Headers.Add("Idempotency-Key", new string('i', 16));
        request.Headers.Add("X-Content-SHA256", new string('A', 64));
        return request;
    }

    private static StreamContent FilePart(string name, string fileName, string contentType, Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\"",
            FileName = $"\"{fileName}\"",
        };
        return content;
    }

    private static MultipartFormDataContent Multipart(params HttpContent[] parts)
    {
        var content = new MultipartFormDataContent("boundary");
        foreach (var part in parts)
        {
            content.Add(part);
        }

        return content;
    }

    private static FiniteChunkedContent RawMultipart(string name, string fileName, string contentType, string body)
    {
        var raw = $"--boundary\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\nContent-Type: {contentType}\r\n\r\n{body}\r\n--boundary--\r\n";
        return RawContent(raw);
    }

    private static FiniteChunkedContent RawContent(string raw)
    {
        var content = new FiniteChunkedContent(Encoding.ASCII.GetBytes(raw));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        return content;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<WebApplication> StartPipelineAsync(CancellationCoordinator? cancellationCoordinator = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(InstantQuotationFilesController).Assembly);
        builder.Services.AddApiVersioning();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, AllowAllPolicyProvider>();
        builder.Services.AddSingleton<IPolicyEvaluator, AllowAllPolicyEvaluator>();
        builder.Services.AddSingleton(new ConsumingService(throwCancellation: false, cancellationCoordinator));
        builder.Services.AddSingleton<IInstantQuoteFileService>(provider => provider.GetRequiredService<ConsumingService>());
        builder.Services.AddSingleton<RecordingMultipartReader>();
        builder.Services.AddSingleton<IInstantQuoteMultipartReader>(provider => provider.GetRequiredService<RecordingMultipartReader>());

        var app = builder.Build();
        if (cancellationCoordinator is not null)
        {
            app.Use((context, next) =>
            {
                context.RequestAborted = cancellationCoordinator.RequestAborted;
                return next();
            });
        }

        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static Stream RawBody(string value) => new NonSeekableByteStream(Encoding.ASCII.GetBytes(value));

    private sealed class ConsumingService(
        bool throwCancellation,
        CancellationCoordinator? cancellationCoordinator = null,
        Exception? uploadException = null) : IInstantQuoteFileService
    {
        public long BytesRead { get; private set; }
        public bool UploadReturned { get; private set; }
        public string? ExpectedSha256 { get; private set; }
        public InstantQuoteUploadMetadata? Metadata { get; private set; }
        public CancellationToken ObservedCancellationToken { get; private set; }
        public Task CancellationObserved => _cancellationObserved.Task;
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CreateInstantQuoteSessionResponse> CreateInstantQuoteSessionAsync(
            InstantQuoteOwner owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<InstantQuoteFileResponse> UploadAsync(
            Guid sessionId,
            InstantQuoteOwner owner,
            string token,
            string idempotencyKey,
            string expectedSha256,
            Stream body,
            InstantQuoteUploadMetadata metadata,
            CancellationToken cancellationToken)
        {
            ObservedCancellationToken = cancellationToken;
            if (uploadException is not null)
            {
                throw uploadException;
            }

            if (throwCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (cancellationCoordinator is not null)
            {
                cancellationCoordinator.MarkServiceStarted();
                try
                {
                    await cancellationCoordinator.WaitForReleaseAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }

            var buffer = new byte[64 * 1024];
            int read;
            try
            {
                while ((read = await body.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    BytesRead += read;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }

            ExpectedSha256 = expectedSha256;
            Metadata = metadata;
            UploadReturned = true;
            return new InstantQuoteFileResponse(Guid.NewGuid(), metadata.FileName, metadata.ContentType, BytesRead, expectedSha256, "pending");
        }

        public Task<FinalizeInstantQuoteFilesResponse> FinalizeAsync(
            Guid sessionId,
            InstantQuoteOwner owner,
            string token,
            string idempotencyKey,
            FinalizeInstantQuoteFilesRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(Guid sessionId, InstantQuoteOwner owner, string token, Guid fileId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FiniteMultipartReader : IInstantQuoteMultipartReader
    {
        public CancellationToken ObservedCancellationToken { get; private set; }

        public Task<InstantQuoteMultipartFile> ReadSingleAsync(
            HttpRequest request,
            string requiredPartName,
            CancellationToken cancellationToken)
        {
            ObservedCancellationToken = cancellationToken;
            return Task.FromResult(new InstantQuoteMultipartFile(
                RawBody("abc"),
                new InstantQuoteUploadMetadata("part.stl", "model/stl")));
        }
    }

    private sealed class RecordingMultipartReader : IInstantQuoteMultipartReader
    {
        public string? ExceptionMessage { get; private set; }
        public bool RequestBodyCanSeek { get; private set; }
        public long? RequestContentLength { get; private set; }
        public CancellationToken ObservedCancellationToken { get; private set; }

        public async Task<InstantQuoteMultipartFile> ReadSingleAsync(
            HttpRequest request,
            string requiredPartName,
            CancellationToken cancellationToken)
        {
            try
            {
                RequestBodyCanSeek = request.Body.CanSeek;
                RequestContentLength = request.ContentLength;
                ObservedCancellationToken = cancellationToken;
                return await new SingleFileMultipartReader().ReadSingleAsync(request, requiredPartName, cancellationToken);
            }
            catch (Exception exception)
            {
                ExceptionMessage = exception.Message;
                throw;
            }
        }
    }

    private sealed class FiniteChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancellationCoordinator : IDisposable
    {
        private readonly CancellationTokenSource _requestAborted = new();
        private readonly TaskCompletionSource _serviceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken RequestAborted => _requestAborted.Token;
        public Task ServiceStarted => _serviceStarted.Task;

        public void MarkServiceStarted() => _serviceStarted.TrySetResult();
        public void CancelRequest() => _requestAborted.Cancel();
        public void Release() => _release.TrySetResult();

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            _release.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
            Release();
            _requestAborted.Dispose();
        }
    }

    private sealed class GeneratedStream(long remaining) : Stream
    {
        private long _remaining = remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill(0x5a);
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableByteStream(byte[] bytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MultipartPayloadStream(long bodyBytes) : Stream
    {
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes(
            "--boundary\r\nContent-Disposition: form-data; name=\"files\"; filename=\"part.stl\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        private static readonly byte[] Suffix = "\r\n--boundary--\r\n"u8.ToArray();
        private int _prefixPosition;
        private long _bodyRemaining = bodyBytes;
        private int _suffixPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_prefixPosition < Prefix.Length)
            {
                var count = Math.Min(buffer.Length, Prefix.Length - _prefixPosition);
                Prefix.AsSpan(_prefixPosition, count).CopyTo(buffer.Span);
                _prefixPosition += count;
                return ValueTask.FromResult(count);
            }

            if (_bodyRemaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, _bodyRemaining);
                buffer.Span[..count].Fill(0x5a);
                _bodyRemaining -= count;
                return ValueTask.FromResult(count);
            }

            var suffixCount = Math.Min(buffer.Length, Suffix.Length - _suffixPosition);
            Suffix.AsSpan(_suffixPosition, suffixCount).CopyTo(buffer.Span);
            _suffixPosition += suffixCount;
            return ValueTask.FromResult(suffixCount);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AllowAllPolicyProvider : IAuthorizationPolicyProvider
    {
        private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(Policy);
    }

    private sealed class AllowAllPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "pipeline-user", ClaimValueTypes.String, "https://issuer.example")],
                "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) => Task.FromResult(PolicyAuthorizationResult.Success());
    }
}
