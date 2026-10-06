using System.Net;
using System.Net.Sockets;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class HostedAcceptanceScannerLeaseTests
{
    [Fact]
    public void OrdinaryAdapterAndScannerConstructorSignaturesRemainAvailable()
    {
        Assert.NotNull(typeof(GoogleCloudObjectStorage).GetConstructor(
            [typeof(Google.Cloud.Storage.V1.StorageClient), typeof(Google.Cloud.Storage.V1.UrlSigner), typeof(IStorageMoveJournal)]));
        Assert.NotNull(typeof(ClamAvFileSafetyScanner).GetConstructor(
            [typeof(IOptions<MalwareScannerOptions>), typeof(Microsoft.Extensions.Logging.ILogger<ClamAvFileSafetyScanner>)]));
    }

    [Fact]
    public async Task ExpiredAdmissionDoesNotConnectToActualListeningScanner()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clock = new Clock();
            var scanner = new ClamAvFileSafetyScanner(Options.Create(new MalwareScannerOptions
            {
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 1,
            }), NullLogger<ClamAvFileSafetyScanner>.Instance, new HostedAcceptanceDependencyLease(clock.GetUtcNow(), clock));
            using var content = new MemoryStream("benign bytes"u8.ToArray());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(content, deadline.Token);
            Assert.Equal(InstantQuoteScanResult.Unavailable, result);
            Assert.False(listener.Pending());
            Assert.Equal(0, content.Position);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    }
}
