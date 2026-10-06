using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Data;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Data;

// Actual installed StorageClient, SDK uploader and guard over a controlled HTTP protocol fixture.
// These are not independently observed backend or financial-completion receipts.
public sealed class HostedAcceptanceSdkResumableTests
{
    [Fact]
    public async Task ActualSdkCompletesMultipleChunksAcross308RangeResponses()
    {
        var clock = new Clock();
        var bytes = Enumerable.Range(0, (512 * 1024) + 17).Select(index => (byte)(index % 251)).ToArray();
        var endpoint = new Endpoint(clock, expireBody: false);
        using var client = Client(clock, endpoint);
        using var content = new MemoryStream(bytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await client.UploadObjectAsync(new StorageObject { Bucket = "synthetic-private", Name = "quarantine/part.pdf", ContentType = "application/pdf" },
            content, new UploadObjectOptions { IfGenerationMatch = 0, ChunkSize = 256 * 1024 }, deadline.Token);
        Assert.Equal(17L, result.Generation);
        Assert.Equal((ulong)bytes.Length, result.Size);
        Assert.Equal(bytes, endpoint.Received.ToArray());
        Assert.Equal(1, endpoint.Initiations);
        Assert.Equal(3, endpoint.Chunks);
        Assert.Equal(2, endpoint.ResumeIncompleteResponses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSdkRejectsMetadataOrFinalUploadBodyCompletingAfterLease(bool upload)
    {
        var clock = new Clock();
        var endpoint = new Endpoint(clock, expireBody: true);
        using var client = Client(clock, endpoint);
        using var content = new MemoryStream("benign bytes"u8.ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (upload)
                _ = await client.UploadObjectAsync(new StorageObject { Bucket = "synthetic-private", Name = "quarantine/part.pdf" },
                    content, new UploadObjectOptions { IfGenerationMatch = 0, ChunkSize = 256 * 1024 }, deadline.Token);
            else
                _ = await client.GetObjectAsync("synthetic-private", "quarantine/part.pdf", cancellationToken: deadline.Token);
        });
        Assert.Contains("lease", failure.GetBaseException().Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(endpoint.BodyReadTriggeredExpiry);
        Assert.Equal(upload ? 1 : 0, endpoint.Chunks);
    }

    [Fact]
    public async Task ZeroByteStatusProbeAllows308WithoutFollowingLocation()
    {
        var clock = new Clock();
        var guard = new HostedAcceptanceStorageTransport(new Uri("http://127.0.0.1:5010/"), clock.Current.AddMinutes(1), clock);
        var handler = new ProbeHandler();
        using var client = new HttpClient(guard.CreateGuardedHandler(handler));
        using var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:5010/upload/session-1") { Content = new ByteArrayContent([]) };
        request.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */524305");
        using var response = await client.SendAsync(request);
        Assert.Equal(308, (int)response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    private static StorageClient Client(Clock clock, HttpMessageHandler endpoint)
    {
        var guard = new HostedAcceptanceStorageTransport(new Uri("http://127.0.0.1:5010/"), clock.Current.AddMinutes(1), clock);
        return new StorageClientBuilder
        {
            BaseUri = "http://127.0.0.1:5010/",
            UnauthenticatedAccess = true,
            HttpClientFactory = new GuardedFactory(guard, endpoint),
        }.Build();
    }

    private sealed class GuardedFactory(HostedAcceptanceStorageTransport guard, HttpMessageHandler endpoint) : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => guard.CreateGuardedHandler(endpoint);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class Endpoint(Clock clock, bool expireBody) : HttpMessageHandler
    {
        public List<byte> Received { get; } = [];
        public int Initiations { get; private set; }
        public int Chunks { get; private set; }
        public int ResumeIncompleteResponses { get; private set; }
        public bool BodyReadTriggeredExpiry { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(("http", "127.0.0.1", 5010), (request.RequestUri!.Scheme, request.RequestUri.Host, request.RequestUri.Port));
            if (request.Method == HttpMethod.Post)
            {
                Initiations++;
                Assert.Contains("ifGenerationMatch=0", request.RequestUri.Query, StringComparison.Ordinal);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("http://127.0.0.1:5010/upload/session-1");
                return response;
            }
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("/upload/session-1", request.RequestUri.AbsolutePath);
                var range = request.Content!.Headers.ContentRange!;
                Assert.Equal((long)Received.Count, range.From);
                Received.AddRange(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                Chunks++;
                Assert.True(range.Length.HasValue);
                if (Received.Count < range.Length.GetValueOrDefault())
                {
                    ResumeIncompleteResponses++;
                    var response = new HttpResponseMessage((HttpStatusCode)308);
                    response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{Received.Count - 1}");
                    return response;
                }
            }
            else Assert.Equal(HttpMethod.Get, request.Method);
            var json = JsonSerializer.Serialize(new
            {
                bucket = "synthetic-private",
                name = "quarantine/part.pdf",
                generation = "17",
                size = Received.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                crc32c = Crc32c(Received.ToArray()),
            });
            var payload = Encoding.UTF8.GetBytes(json);
            var content = expireBody ? (HttpContent)new StreamContent(new ExpiringBody(payload, () =>
            {
                BodyReadTriggeredExpiry = true;
                clock.Current = clock.Current.AddMinutes(2);
            })) : new ByteArrayContent(payload);
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static string Crc32c(byte[] bytes)
        {
            var crc = uint.MaxValue;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82F63B78U);
            }
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoded, ~crc);
            return Convert.ToBase64String(encoded);
        }
    }

    private sealed class ExpiringBody(byte[] bytes, Action expire) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            expire();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage((HttpStatusCode)308);
            response.Headers.Location = new Uri("http://127.0.0.1:5010/upload/session-1");
            return Task.FromResult(response);
        }
    }
}
