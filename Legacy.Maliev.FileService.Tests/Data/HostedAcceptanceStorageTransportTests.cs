using System.Net;
using Legacy.Maliev.FileService.Data;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class HostedAcceptanceStorageTransportTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("http://127.0.0.1:5010/")]
    [InlineData("http://[::1]:5010/")]
    public void AdmitsExactLiteralLoopbackOrigins(string value)
    {
        var transport = new HostedAcceptanceStorageTransport(new Uri(value), Started.AddMinutes(10), new Clock());
        transport.ValidateRequest(new Uri(new Uri(value), "storage/v1/b/fixture"));
    }

    [Theory]
    [InlineData("https://127.0.0.1:5010/")]
    [InlineData("http://localhost:5010/")]
    [InlineData("http://127.0.0.2:5010/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:5010/path")]
    [InlineData("http://user@127.0.0.1:5010/")]
    [InlineData("http://127.0.0.1:5010/?query=1")]
    [InlineData("http://127.0.0.1:5010/#fragment")]
    public void RejectsUnadmittedOriginShape(string value) => Assert.Throws<ArgumentException>(() =>
        new HostedAcceptanceStorageTransport(new Uri(value), Started.AddMinutes(10), new Clock()));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void RejectsExpiredOrOverlongLease(int minutes) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new HostedAcceptanceStorageTransport(new Uri("http://127.0.0.1:5010/"), Started.AddMinutes(minutes), new Clock()));

    [Theory]
    [InlineData("https://127.0.0.1:5010/objects")]
    [InlineData("http://127.0.0.1:5011/objects")]
    [InlineData("http://localhost:5010/objects")]
    [InlineData("http://storage.googleapis.com/objects")]
    public async Task EscapedRequestsNeverReachInnerTransport(string value)
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(Transport(new Clock()).CreateGuardedHandler(inner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(value));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task CurrentOriginReachesInnerTransportAndExpiryRevokesIt()
    {
        var clock = new Clock();
        var inner = new RecordingHandler();
        using var client = new HttpClient(Transport(clock).CreateGuardedHandler(inner));
        using var response = await client.GetAsync("http://127.0.0.1:5010/storage/v1/b/fixture");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
        clock.Now = Started.AddMinutes(10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:5010/objects"));
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(302, "http://127.0.0.1:5010/next")]
    [InlineData(307, "http://127.0.0.1:5010/next")]
    [InlineData(308, "http://127.0.0.1:5010/next")]
    [InlineData(200, "http://127.0.0.1:5011/upload")]
    [InlineData(200, "https://storage.googleapis.com/upload")]
    public async Task RedirectsAndEscapedResumableLocationsFailWithoutFollowing(int status, string location)
    {
        var inner = new RecordingHandler((HttpStatusCode)status, location);
        using var client = new HttpClient(Transport(new Clock()).CreateGuardedHandler(inner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:5010/objects"));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task SameOriginResumableLocationRemainsAvailable()
    {
        var inner = new RecordingHandler(HttpStatusCode.OK, "/upload/session-1");
        using var client = new HttpClient(Transport(new Clock()).CreateGuardedHandler(inner));
        using var response = await client.GetAsync("http://127.0.0.1:5010/objects");
        Assert.Equal(new Uri("/upload/session-1", UriKind.Relative), response.Headers.Location);
        Assert.Equal(1, inner.Calls);
    }

    private static HostedAcceptanceStorageTransport Transport(Clock clock) =>
        new(new Uri("http://127.0.0.1:5010/"), Started.AddMinutes(10), clock);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Started;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string? location = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status);
            if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }
    }
}
