using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Streams complete files to a ClamAV daemon without writing them to local disk.</summary>
public sealed class ClamAvFileSafetyScanner(
    IOptions<MalwareScannerOptions> options,
    ILogger<ClamAvFileSafetyScanner> logger) : IFileSafetyScanner, IInstantQuoteFileSafetyScanner
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxResponseBytes = 4096;

    /// <inheritdoc />
    public async Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken cancellationToken)
    {
        await using var content = file.OpenReadStream();
        return await ScanContentAsync(content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstantQuoteScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        var result = await ScanContentAsync(content, cancellationToken);
        return result.Verdict switch
        {
            FileSafetyVerdict.Clean => InstantQuoteScanResult.Clean,
            FileSafetyVerdict.Infected => InstantQuoteScanResult.Unsafe,
            _ => InstantQuoteScanResult.Unavailable,
        };
    }

    private async Task<FileSafetyResult> ScanContentAsync(Stream content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return new FileSafetyResult(FileSafetyVerdict.Unavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 300)));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
            await using var network = client.GetStream();
            await network.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

            var buffer = new byte[ChunkSize];
            while (true)
            {
                var read = await content.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                var length = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32BigEndian(length, read);
                await network.WriteAsync(length, timeout.Token);
                await network.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }

            await network.WriteAsync(new byte[sizeof(int)], timeout.Token);
            await network.FlushAsync(timeout.Token);

            var response = await ReadResponseAsync(network, timeout.Token);
            return response is null
                ? new FileSafetyResult(FileSafetyVerdict.Unavailable)
                : ParseResponse(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            logger.LogWarning(exception, "ClamAV could not scan upload");
            return new FileSafetyResult(FileSafetyVerdict.Unavailable);
        }
    }

    private static async Task<string?> ReadResponseAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxResponseBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await network.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            var terminatorOffset = buffer.AsSpan(length, read).IndexOf((byte)0);
            if (terminatorOffset >= 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, length + terminatorOffset + 1);
            }

            length += read;
        }

        return null;
    }

    /// <summary>Parses a ClamAV INSTREAM response.</summary>
    public static FileSafetyResult ParseResponse(string response)
    {
        var normalized = response.TrimEnd('\0', '\r', '\n');
        if (normalized.EndsWith(": OK", StringComparison.Ordinal))
        {
            return new FileSafetyResult(FileSafetyVerdict.Clean);
        }

        const string foundSuffix = " FOUND";
        if (normalized.EndsWith(foundSuffix, StringComparison.Ordinal))
        {
            var separator = normalized.IndexOf(": ", StringComparison.Ordinal);
            var threat = separator >= 0
                ? normalized[(separator + 2)..^foundSuffix.Length]
                : "unknown";
            return new FileSafetyResult(FileSafetyVerdict.Infected, threat);
        }

        return new FileSafetyResult(FileSafetyVerdict.Unavailable);
    }
}
