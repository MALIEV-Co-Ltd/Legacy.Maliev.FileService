using Legacy.Maliev.FileService.Application.Interfaces;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Bounds an exact-generation download before it can consume unbounded local storage.</summary>
internal sealed class BoundedDownloadWriteStream(Stream destination, long maximumBytes) : Stream
{
    private long written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => written;
    public override long Position { get => written; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckLength(count);
        destination.Write(buffer, offset, count);
        written += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckLength(buffer.Length);
        destination.Write(buffer);
        written += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckLength(buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken);
        written += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void CheckLength(int count)
    {
        if (count > maximumBytes - written)
        {
            throw new InstantQuoteDependencyUnavailableException("The stored file exceeds its recorded size.");
        }
    }
}
