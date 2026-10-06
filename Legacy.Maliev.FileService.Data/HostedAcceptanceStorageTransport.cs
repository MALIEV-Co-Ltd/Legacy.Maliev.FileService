using System.Net;
using Google.Apis.Http;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Confines an explicitly admitted hosted acceptance SDK client to one loopback origin.</summary>
/// <remarks>Normal runtime registration does not select this transport. The launcher owns endpoint admission.</remarks>
public sealed class HostedAcceptanceStorageTransport : Google.Apis.Http.HttpClientFactory
{
    private readonly Uri origin;
    private readonly DateTimeOffset expiresUtc;
    private readonly TimeProvider clock;

    /// <summary>Creates an isolated transport with an already admitted endpoint and finite lease.</summary>
    public HostedAcceptanceStorageTransport(Uri origin, DateTimeOffset expiresUtc, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(clock);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttp ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.Port is <= 0 or > 65535 ||
            origin.Host is not ("127.0.0.1" or "[::1]") ||
            !(origin.OriginalString == $"http://{origin.Host}:{origin.Port}" ||
              origin.OriginalString == $"http://{origin.Host}:{origin.Port}/"))
        {
            throw new ArgumentException("Hosted storage requires one literal loopback HTTP origin.", nameof(origin));
        }

        var now = clock.GetUtcNow();
        if (expiresUtc <= now || expiresUtc - now > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "Hosted storage lease must be current and bounded.");
        }

        this.origin = origin;
        this.expiresUtc = expiresUtc;
        this.clock = clock;
    }

    /// <summary>Checks the exact admitted scheme, literal host, port and current lease before any request.</summary>
    public void ValidateRequest(Uri request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (clock.GetUtcNow() >= expiresUtc || !request.IsAbsoluteUri ||
            request.UserInfo.Length != 0 || request.Fragment.Length != 0 ||
            request.Scheme != origin.Scheme || request.Host != origin.Host || request.Port != origin.Port)
        {
            throw new InvalidOperationException("Storage request escaped its admitted hosted origin or lease.");
        }
    }

    /// <inheritdoc />
    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
        CreateGuardedHandler(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        });

    internal HttpMessageHandler CreateGuardedHandler(HttpMessageHandler inner) => new OriginHandler(this, inner);

    private void ValidateLease()
    {
        if (clock.GetUtcNow() >= expiresUtc)
            throw new InvalidOperationException("Hosted storage response body exceeded its admitted lease.");
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        ValidateLease();
        var remaining = expiresUtc - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("Hosted storage lease expired before its operation deadline.");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
        return deadline;
    }

    private sealed class OriginHandler(HostedAcceptanceStorageTransport owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.ValidateRequest(request.RequestUri ?? throw new InvalidOperationException("Storage request URI is absent."));
            using var deadline = owner.Deadline(cancellationToken);
            var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            try
            {
                owner.ValidateRequest(request.RequestUri!);
                var resumeIncomplete = (int)response.StatusCode == 308 && request.Method == HttpMethod.Put &&
                    request.Content?.Headers.ContentRange?.Unit == "bytes";
                if ((int)response.StatusCode is >= 300 and < 400 && !resumeIncomplete)
                    throw new InvalidOperationException("Hosted storage redirects are forbidden.");
                if (response.Headers.Location is { } location)
                    owner.ValidateRequest(location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location));
                response.Content = new LeaseCheckedContent(response.Content, owner);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private sealed class LeaseCheckedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly HostedAcceptanceStorageTransport owner;
        public LeaseCheckedContent(HttpContent inner, HostedAcceptanceStorageTransport owner)
        {
            this.inner = inner;
            this.owner = owner;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(stream, cancellationToken);
        private async Task CopyAsync(Stream destination, CancellationToken cancellationToken)
        {
            using var deadline = owner.Deadline(cancellationToken);
            await using var source = new LeaseCheckedReadStream(await inner.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), owner);
            await source.CopyToAsync(destination, 16384, deadline.Token).ConfigureAwait(false);
            owner.ValidateLease();
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateStreamAsync(CancellationToken.None);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateStreamAsync(cancellationToken);
        private async Task<Stream> CreateStreamAsync(CancellationToken cancellationToken)
        {
            using var deadline = owner.Deadline(cancellationToken);
            var source = await inner.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                owner.ValidateLease();
                return new LeaseCheckedReadStream(source, owner);
            }
            catch
            {
                await source.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            try { if (disposing) inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }
    }

    private sealed class LeaseCheckedReadStream(Stream inner, HostedAcceptanceStorageTransport owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var deadline = owner.Deadline(cancellationToken);
            var read = await inner.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            owner.ValidateLease();
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
