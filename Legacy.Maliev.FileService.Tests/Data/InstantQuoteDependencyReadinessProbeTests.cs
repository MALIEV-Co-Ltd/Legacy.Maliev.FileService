using System.Net;
using System.Net.Sockets;
using System.Text;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class InstantQuoteDependencyReadinessProbeTests
{
    [Fact]
    public async Task ClamAvProbe_CheckAsync_SendsPingAndRequiresPong()
    {
        await using var server = new PingServer("PONG\0");
        var probe = new InstantQuoteClamAvReadinessProbe(
            Options.Create(new MalwareScannerOptions
            {
                Host = IPAddress.Loopback.ToString(),
                Port = server.Port,
                TimeoutSeconds = 2,
            }));

        await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("zPING\0", await server.Command);
    }

    [Fact]
    public async Task ClamAvProbe_CheckAsync_AcceptsFragmentedPong()
    {
        await using var server = new PingServer(["PO", "NG\0"]);
        var probe = new InstantQuoteClamAvReadinessProbe(
            Options.Create(new MalwareScannerOptions
            {
                Host = IPAddress.Loopback.ToString(),
                Port = server.Port,
                TimeoutSeconds = 2,
            }));

        await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("zPING\0", await server.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN\0")]
    public async Task ClamAvProbe_CheckAsync_MissingOrUnexpectedResponseFailsClosed(string response)
    {
        await using var server = new PingServer(response);
        var probe = new InstantQuoteClamAvReadinessProbe(
            Options.Create(new MalwareScannerOptions
            {
                Host = IPAddress.Loopback.ToString(),
                Port = server.Port,
                TimeoutSeconds = 2,
            }));

        await Assert.ThrowsAsync<IOException>(() => probe.CheckAsync(CancellationToken.None));
    }

    private sealed class PingServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task<string> _command;

        public PingServer(string response)
            : this([response])
        {
        }

        public PingServer(IReadOnlyList<string> responses)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _command = ServeAsync(responses);
        }

        public int Port { get; }
        public Task<string> Command => _command;

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try
            {
                await _command;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            _cancellation.Dispose();
        }

        private async Task<string> ServeAsync(IReadOnlyList<string> responses)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, _cancellation.Token);
            var command = Encoding.ASCII.GetString(buffer, 0, read);
            foreach (var response in responses)
            {
                if (response.Length > 0)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cancellation.Token);
                    await stream.FlushAsync(_cancellation.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _cancellation.Token);
                }
            }
            client.Client.Shutdown(SocketShutdown.Send);
            return command;
        }
    }
}
