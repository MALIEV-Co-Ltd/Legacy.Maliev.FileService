using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Real installed Google SDK over an owned, in-memory HTTP transport. No ADC, keys,
// sockets, DNS, GCS, NAS or database. This is wire/byte evidence, not a clean-scan
// certificate, owner-system join, or production acceptance result.
public sealed class ProtectedDocumentGoogleSdkTransportTests
{
    [Fact]
    public async Task ActualSdkDirectMediaPinsGenerationAndMatchAndReturnsCompleteDigest()
    {
        using var fixture = new Fixture();
        Assert.IsType<StorageClientImpl>(fixture.Client);
        var bytes = await fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, CustomerDocumentOptions.MaximumBytes, default);
        Assert.Equal(Payload, bytes.ToArray());
        Assert.Equal(SHA256.HashData(Payload), SHA256.HashData(bytes.Span));
        Assert.Equal(Payload.Length, fixture.Endpoint.MediaBytesRead);
        Assert.True(fixture.Endpoint.MediaReadCalls > 1);
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Fact]
    public async Task ActualSdkMediaCannotEscapeFeatureByteBound()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, 17, default));
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("sameSizeReplacement")]
    [InlineData("readFailure")]
    public async Task ActualSdkIncompleteOrSubstitutedMediaNeverReturnsPartialBytes(string failure)
    {
        using var fixture = new Fixture(mediaFailure: failure);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, CustomerDocumentOptions.MaximumBytes, default));
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        Assert.True(fixture.Endpoint.MediaBytesRead > 0);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Fact]
    public async Task ActualSdkMalformedChecksumNeverReturnsBytes()
    {
        using var fixture = new Fixture(mediaFailure: "malformedChecksum");
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, CustomerDocumentOptions.MaximumBytes, default));
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task ActualSdkMissingOrReplacedGenerationDeniesWithoutLatestFallback(HttpStatusCode status)
    {
        using var fixture = new Fixture(errorStatus: status);
        await Assert.ThrowsAsync<DocumentAuthorityDeniedException>(() => fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, CustomerDocumentOptions.MaximumBytes, default));
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Fact]
    public async Task ActualSdkUnknownDependencyFailureIsUnavailableWithoutSuccessOrFallback()
    {
        // Non-retryable synthetic dependency failure keeps this fixture finite. It
        // does not assert anything about production retry/availability behavior.
        using var fixture = new Fixture(errorStatus: HttpStatusCode.FailedDependency);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, CustomerDocumentOptions.MaximumBytes, default));
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Theory]
    [InlineData("missingGeneration")]
    [InlineData("malformedGeneration")]
    [InlineData("differentGeneration")]
    public async Task ActualSdkUnconfirmedResponseGenerationNeverReturnsBytes(string failure)
    {
        using var fixture = new Fixture(mediaFailure: failure);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => fixture.Reader.ReadAsync(Bucket, ObjectName, Generation, CustomerDocumentOptions.MaximumBytes, default));
        Assert.Equal(0, fixture.Endpoint.MetadataRequests);
        Assert.Equal(1, fixture.Endpoint.MediaRequests);
        AssertPinnedWire(fixture.Endpoint);
    }

    [Fact]
    public async Task BareSdkMissingChecksumIsNotAnIntegrityCertificate()
    {
        // SDK Always mode tolerates an absent hash. This diagnostic calls the bare
        // SDK, not protected storage, and never treats returned bytes as clean proof.
        using var fixture = new Fixture(mediaFailure: "missingChecksumSubstitution");
        using var output = new MemoryStream();
        var metadata = await fixture.Client.DownloadObjectAsync(Bucket, ObjectName, output,
            new DownloadObjectOptions { Generation = Generation, IfGenerationMatch = Generation });
        Assert.Equal(Generation, metadata.Generation);
        Assert.Null(metadata.Crc32c);
        Assert.Equal(Payload.Length, output.Length);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(Payload)), Convert.ToHexString(SHA256.HashData(output.ToArray())));
        AssertPinnedWire(fixture.Endpoint);
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("Unknown", true)]
    [InlineData("MetadataCommitted", false)]
    public async Task UnconfirmedJournalNeverBecomesCleanEvidenceBecauseSdkBytesExist(string state, bool clean)
    {
        using var fixture = new Fixture();
        var operation = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var journal = new Mock<IStorageMoveJournal>(MockBehavior.Strict);
        StorageMoveEvidence? fault = state == "absent" ? null : new(clean, Bucket,
            $"{ObjectName[..^9]}/quarantine/{operation:N}", 70, Bucket, ObjectName, Generation, state);
        journal.Setup(x => x.FindAsync(operation, It.IsAny<CancellationToken>())).ReturnsAsync(fault);
        var objects = new Mock<IObjectStorage>(MockBehavior.Strict);
        var scanner = new Mock<IFileSafetyScanner>(MockBehavior.Strict);
        var adapter = new CustomerDocumentStorageAdapter(objects.Object, scanner.Object, journal.Object, fixture.Reader, FeatureOptions());
        var stored = new DocumentStoredContent(Bucket, ObjectName, Generation, 70, operation, Payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(Payload)), "application/pdf", "synthetic.pdf");
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => adapter.ReadAsync(stored, default));
        Assert.Empty(fixture.Endpoint.Requests);
        objects.VerifyNoOtherCalls();
        scanner.VerifyNoOtherCalls();
        // Fault records above are injected refusal inputs; none is certified as
        // real scanner/journal evidence and no positive scan verdict is provided.
    }

    private const string Bucket = "synthetic-private";
    private const long Generation = 71;
    private const string ObjectName = "customer-documents/23/11111111111111111111111111111111/22222222222222222222222222222222/original";
    private const string Origin = "https://customer-document-sdk-fixture.example.invalid/";
    private static readonly byte[] Payload = Enumerable.Range(0, (32 * 1024) + 19).Select(x => (byte)(x % 251)).ToArray();
    private static IOptions<CustomerDocumentOptions> FeatureOptions() => Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = Bucket });

    private static void AssertPinnedWire(Endpoint endpoint)
    {
        Assert.NotEmpty(endpoint.Requests);
        foreach (var request in endpoint.Requests)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri(Origin).GetLeftPart(UriPartial.Authority), request.Uri.GetLeftPart(UriPartial.Authority));
            Assert.EndsWith($"/b/{Bucket}/o/{ObjectName}", Uri.UnescapeDataString(request.Uri.AbsolutePath), StringComparison.Ordinal);
            Assert.Null(request.Authorization);
            Assert.Equal(string.Empty, request.Uri.Fragment);
            var query = Query(request.Uri);
            Assert.Equal(Generation.ToString(CultureInfo.InvariantCulture), query["generation"]);
            Assert.Equal(Generation.ToString(CultureInfo.InvariantCulture), query["ifGenerationMatch"]);
        }
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
            pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal);

    private sealed class Fixture : IDisposable
    {
        public Endpoint Endpoint { get; }
        public StorageClient Client { get; }
        public CustomerDocumentGoogleCloudGenerationReader Reader { get; }
        public Fixture(string? mediaFailure = null, HttpStatusCode? errorStatus = null)
        {
            Endpoint = new(mediaFailure, errorStatus);
            // Public real SDK DI constructor; this initializer provides no
            // credential initializer and its sole transport has no network inner.
            var service = new StorageService(new BaseClientService.Initializer
            {
                BaseUri = Origin + "storage/v1/",
                ApplicationName = "feature-only-controlled-generation-fixture",
                HttpClientInitializer = null,
                HttpClientFactory = new OwnedFactory(Endpoint),
                GZipEnabled = false,
            });
            Client = new StorageClientImpl(service, null);
            Reader = new(Client, FeatureOptions());
        }
        public void Dispose() => Client.Dispose();
    }

    private sealed class OwnedFactory(Endpoint endpoint) : Google.Apis.Http.IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            var handler = new ConfigurableMessageHandler(new OwnedSdkInterceptionBridge(endpoint)) { NumTries = 1, NumRedirects = 1, FollowRedirect = false };
            var client = new ConfigurableHttpClient(handler);
            foreach (var initializer in args.Initializers) initializer.Initialize(client);
            return client;
        }
    }

    // Google.Apis' normal factory installs its INTERNAL StreamInterceptionHandler.
    // This owned equivalent preserves that public request-option contract without
    // constructing a socket handler or reflecting into SDK internals. The provider
    // and interceptor below belong to the REAL SDK HashValidatingDownloader: this
    // bridge does not calculate/compare hashes or manufacture success/failure.
    private sealed class OwnedSdkInterceptionBridge(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            try
            {
                var key = new HttpRequestOptionsKey<Func<HttpResponseMessage, StreamInterceptor>>(ConfigurableMessageHandler.ResponseStreamInterceptorProviderKey);
                if (request.Options.TryGetValue(key, out var provider))
                {
                    var interceptor = provider(response);
                    if (interceptor is not null) response.Content = new InterceptedContent(response.Content, interceptor);
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private sealed class InterceptedContent : HttpContent
    {
        private readonly HttpContent original;
        private readonly StreamInterceptor interceptor;
        public InterceptedContent(HttpContent original, StreamInterceptor interceptor)
        {
            this.original = original;
            this.interceptor = interceptor;
            foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = original.Headers.ContentLength ?? 0; return original.Headers.ContentLength.HasValue; }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new InterceptedBody(await original.ReadAsStreamAsync(), interceptor);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            using var input = await CreateContentReadStreamAsync();
            await input.CopyToAsync(stream);
        }
        protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
    }

    private sealed class InterceptedBody(Stream original, StreamInterceptor interceptor) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => original.Length;
        public override long Position { get => original.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = original.Read(buffer, offset, count);
            interceptor(buffer, offset, read);
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            var read = await original.ReadAsync(buffer, offset, count, token);
            interceptor(buffer, offset, read);
            return read;
        }
        public override void Flush() => original.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, AuthenticationHeaderValue? Authorization);

    private sealed class Endpoint(string? mediaFailure, HttpStatusCode? errorStatus) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public int MetadataRequests { get; private set; }
        public int MediaRequests { get; private set; }
        public int MediaBytesRead { get; private set; }
        public int MediaReadCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException("Synthetic SDK request omitted its URI.");
            Requests.Add(new(request.Method, uri, request.Headers.Authorization));
            if (request.Method != HttpMethod.Get || uri.GetLeftPart(UriPartial.Authority) != new Uri(Origin).GetLeftPart(UriPartial.Authority) || Requests.Count > 4)
                throw new InvalidOperationException("SDK fixture request escaped its closed origin/method/request budget.");
            var media = Query(uri).GetValueOrDefault("alt") == "media";
            if (media) MediaRequests++; else MetadataRequests++;
            if (!media) throw new InvalidOperationException("Actual SDK direct download unexpectedly requested metadata.");
            var response = errorStatus is { } status ? Error(status) : Media();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpResponseMessage Media()
        {
            var body = mediaFailure == "truncated" ? Payload[..(Payload.Length / 2)] : Payload.ToArray();
            if (mediaFailure is "sameSizeReplacement" or "missingChecksumSubstitution") body[^1] ^= 1;
            var content = new StreamContent(new FragmentedBody(body, mediaFailure == "readFailure", count => { MediaBytesRead += count; MediaReadCalls++; }));
            content.Headers.ContentType = new("application/octet-stream");
            content.Headers.ContentLength = Payload.Length;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            if (mediaFailure != "missingChecksumSubstitution")
                response.Headers.TryAddWithoutValidation("x-goog-hash", "crc32c=" + (mediaFailure == "malformedChecksum" ? "not-base64" : Crc32c(Payload)));
            if (mediaFailure != "missingGeneration")
                response.Headers.TryAddWithoutValidation("x-goog-generation", mediaFailure == "malformedGeneration" ? "not-a-generation" : (mediaFailure == "differentGeneration" ? Generation + 1 : Generation).ToString(CultureInfo.InvariantCulture));
            return response;
        }

        private static HttpResponseMessage Error(HttpStatusCode status)
        {
            var json = JsonSerializer.Serialize(new { error = new { code = (int)status, message = "Synthetic fixture dependency failure", errors = new[] { new { domain = "global", reason = status == HttpStatusCode.NotFound ? "notFound" : status == HttpStatusCode.PreconditionFailed ? "conditionNotMet" : "syntheticDependencyFailure" } } } });
            return new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string Crc32c(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82F63B78U);
        }
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, ~crc);
        return Convert.ToBase64String(encoded);
    }

    // A Stream wrapper deliberately avoids MemoryStream's optimized CopyToAsync,
    // so SDK copy paths must consume the fragmented/failed body through Read.
    private sealed class FragmentedBody(byte[] bytes, bool failAfterPrefix, Action<int> observed) : Stream
    {
        private readonly MemoryStream body = new(bytes, false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int NextCount(int requested)
        {
            if (failAfterPrefix && body.Position >= 61) throw new IOException("Synthetic incomplete provider body after a confirmed prefix.");
            return Math.Min(requested, 61);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = body.Read(buffer, offset, NextCount(count)); observed(read); return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = body.Read(buffer[..NextCount(buffer.Length)]); observed(read); return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span));
        }
        protected override void Dispose(bool disposing) { if (disposing) body.Dispose(); base.Dispose(disposing); }
    }
}
