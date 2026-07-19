using System.Net.Sockets;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Checks ClamAV availability using its bounded, read-only PING command.</summary>
public sealed class InstantQuoteClamAvReadinessProbe(IOptions<MalwareScannerOptions> options)
    : IInstantQuoteScannerReadinessProbe
{
    private static readonly byte[] PingCommand = "zPING\0"u8.ToArray();

    /// <inheritdoc />
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new IOException("ClamAV host is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 300)));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(PingCommand, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            var normalized = await ReadResponseAsync(stream, timeout.Token);
            if (!string.Equals(normalized, "PONG", StringComparison.Ordinal))
            {
                throw new IOException("ClamAV returned an unexpected readiness response.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            throw new IOException("ClamAV readiness check failed.", exception);
        }
    }

    private static async Task<string> ReadResponseAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var response = new byte[16];
        var length = 0;
        while (length < response.Length)
        {
            var read = await stream.ReadAsync(response.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                throw new IOException("ClamAV closed the readiness connection without a complete response.");
            }

            var terminator = response.AsSpan(length, read).IndexOf((byte)0);
            if (terminator >= 0)
            {
                return Encoding.ASCII.GetString(response, 0, length + terminator);
            }

            length += read;
        }

        throw new IOException("ClamAV readiness response exceeded the allowed length.");
    }
}
