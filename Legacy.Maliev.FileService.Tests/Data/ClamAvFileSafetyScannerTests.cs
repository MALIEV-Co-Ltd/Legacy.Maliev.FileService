using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class ClamAvFileSafetyScannerTests
{
    [Fact]
    public void ParseResponse_Ok_ReturnsClean()
    {
        var result = ClamAvFileSafetyScanner.ParseResponse("stream: OK\0");

        Assert.Equal(FileSafetyVerdict.Clean, result.Verdict);
    }

    [Fact]
    public void ParseResponse_Found_ReturnsThreat()
    {
        var result = ClamAvFileSafetyScanner.ParseResponse("stream: Eicar-Test-Signature FOUND\0");

        Assert.Equal(FileSafetyVerdict.Infected, result.Verdict);
        Assert.Equal("Eicar-Test-Signature", result.ThreatName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stream: Size limit exceeded. ERROR\0")]
    public void ParseResponse_Unknown_ReturnsUnavailable(string response)
    {
        var result = ClamAvFileSafetyScanner.ParseResponse(response);

        Assert.Equal(FileSafetyVerdict.Unavailable, result.Verdict);
    }

    [Fact]
    public async Task ScanAsync_StreamedCleanContent_UsesInstreamChunksAndLeavesCallerStreamOpen()
    {
        var contentBytes = Enumerable.Range(0, (64 * 1024) + 13)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var receivedChunks = new List<byte[]>();
        await using var server = new LoopbackClamAvServer(async (stream, cancellationToken) =>
        {
            await AssertCommandAsync(stream, cancellationToken);
            receivedChunks.AddRange(await ReadChunksAsync(stream, cancellationToken));
            foreach (var fragment in new[] { "stream:", " OK", "\0" })
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(fragment), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        });
        using var content = new TrackingMemoryStream(contentBytes);
        var scanner = CreateScanner(server.Port);

        var result = await ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(content, CancellationToken.None);

        Assert.Equal(InstantQuoteScanResult.Clean, result);
        Assert.False(content.WasDisposed);
        Assert.Equal(new[] { 64 * 1024, 13 }, receivedChunks.Select(chunk => chunk.Length));
        Assert.Equal(contentBytes, receivedChunks.SelectMany(chunk => chunk));
        await server.Completion;
    }

    [Fact]
    public async Task ScanAsync_InfectedResponse_ReturnsUnsafe()
    {
        await using var server = new LoopbackClamAvServer(async (stream, cancellationToken) =>
        {
            await AssertCommandAsync(stream, cancellationToken);
            _ = await ReadChunksAsync(stream, cancellationToken);
            await stream.WriteAsync("stream: Eicar-Test-Signature FOUND\0"u8.ToArray(), cancellationToken);
        });
        var scanner = CreateScanner(server.Port);

        var result = await ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(
            new MemoryStream("unsafe"u8.ToArray()),
            CancellationToken.None);

        Assert.Equal(InstantQuoteScanResult.Unsafe, result);
        await server.Completion;
    }

    [Theory]
    [InlineData("stream: Size limit exceeded. ERROR\0")]
    [InlineData("stream: OK")]
    public async Task ScanAsync_UntrustworthyResponse_ReturnsUnavailable(string response)
    {
        await using var server = new LoopbackClamAvServer(async (stream, cancellationToken) =>
        {
            await AssertCommandAsync(stream, cancellationToken);
            _ = await ReadChunksAsync(stream, cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken);
        });
        var scanner = CreateScanner(server.Port);

        var result = await ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(
            new MemoryStream("content"u8.ToArray()),
            CancellationToken.None);

        Assert.Equal(InstantQuoteScanResult.Unavailable, result);
        await server.Completion;
    }

    [Fact]
    public async Task ScanAsync_ResponseExceedsLimit_ReturnsUnavailable()
    {
        await using var server = new LoopbackClamAvServer(async (stream, cancellationToken) =>
        {
            await AssertCommandAsync(stream, cancellationToken);
            _ = await ReadChunksAsync(stream, cancellationToken);
            await stream.WriteAsync(Enumerable.Repeat((byte)'x', 4097).ToArray(), cancellationToken);
        });
        var scanner = CreateScanner(server.Port);

        var result = await ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(
            new MemoryStream("content"u8.ToArray()),
            CancellationToken.None);

        Assert.Equal(InstantQuoteScanResult.Unavailable, result);
        await server.Completion;
    }

    [Fact]
    public async Task ScanAsync_CallerCancellation_PropagatesCancellation()
    {
        await using var server = new LoopbackClamAvServer(async (stream, cancellationToken) =>
        {
            await AssertCommandAsync(stream, cancellationToken);
            _ = await ReadChunksAsync(stream, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var scanner = CreateScanner(server.Port, timeoutSeconds: 30);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(new MemoryStream("content"u8.ToArray()), cancellation.Token));
    }

    [Fact]
    public async Task ScanAsync_ScannerTimeout_ReturnsUnavailable()
    {
        await using var server = new LoopbackClamAvServer(async (stream, cancellationToken) =>
        {
            await AssertCommandAsync(stream, cancellationToken);
            _ = await ReadChunksAsync(stream, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var scanner = CreateScanner(server.Port, timeoutSeconds: 1);

        var result = await ((IInstantQuoteFileSafetyScanner)scanner).ScanAsync(
            new MemoryStream("content"u8.ToArray()),
            CancellationToken.None);

        Assert.Equal(InstantQuoteScanResult.Unavailable, result);
    }

    private static ClamAvFileSafetyScanner CreateScanner(int port, int timeoutSeconds = 5) =>
        new(
            Options.Create(new MalwareScannerOptions
            {
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                TimeoutSeconds = timeoutSeconds,
            }),
            NullLogger<ClamAvFileSafetyScanner>.Instance);

    private static async Task AssertCommandAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var command = new byte["zINSTREAM\0"u8.Length];
        await ReadExactlyAsync(stream, command, cancellationToken);
        Assert.Equal("zINSTREAM\0"u8.ToArray(), command);
    }

    private static async Task<IReadOnlyList<byte[]>> ReadChunksAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var chunks = new List<byte[]>();
        var lengthBuffer = new byte[sizeof(int)];
        while (true)
        {
            await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            if (length == 0)
            {
                return chunks;
            }

            Assert.InRange(length, 1, 64 * 1024);
            var chunk = new byte[length];
            await ReadExactlyAsync(stream, chunk, cancellationToken);
            chunks.Add(chunk);
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class LoopbackClamAvServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TcpListener _listener;

        public LoopbackClamAvServer(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = RunAsync(handler);
        }

        public int Port { get; }

        public Task Completion { get; }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try
            {
                await Completion;
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }

            _cancellation.Dispose();
        }

        private async Task RunAsync(Func<NetworkStream, CancellationToken, Task> handler)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            await handler(stream, _cancellation.Token);
        }
    }
}
