using System.Buffers;
using System.Security.Cryptography;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Streams a source while enforcing an actual-byte limit and computing SHA-256 incrementally.</summary>
public sealed class BoundedHashingReadStream : Stream
{
    private readonly Stream _source;
    private readonly long _maximumBytes;
    private readonly int _maximumReadSize;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _payloadTooLarge;
    private string? _sha256;

    /// <summary>Creates a bounded hashing wrapper that owns the source stream.</summary>
    public BoundedHashingReadStream(
        Stream source,
        long maximumBytes = InstantQuoteFileContract.MaximumUploadBytes,
        int maximumReadSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadSize);
        _source = source;
        _maximumBytes = maximumBytes;
        _maximumReadSize = maximumReadSize;
    }

    /// <summary>Gets the actual number of bytes read from the source.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Gets the lower-case SHA-256 after the source reaches end of stream.</summary>
    public string Sha256 => _sha256 ?? throw new InvalidOperationException("The stream has not reached its end.");

    /// <summary>Gets whether the source has reached end of stream and the digest is final.</summary>
    public bool IsComplete => _sha256 is not null;

    /// <inheritdoc />
    public override bool CanRead => _source.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, _maximumReadSize));
        try
        {
            var read = Read(rented, 0, Math.Min(buffer.Length, _maximumReadSize));
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_payloadTooLarge)
        {
            throw new InstantQuotePayloadTooLargeException("Uploaded file exceeds the maximum size.");
        }

        var remaining = _maximumBytes - BytesRead;
        var maximumRead = remaining < _maximumReadSize
            ? checked((int)remaining + 1)
            : _maximumReadSize;
        var read = await _source.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
        if (read == 0)
        {
            _sha256 ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            return 0;
        }

        BytesRead += read;
        if (BytesRead > _maximumBytes)
        {
            _payloadTooLarge = true;
            throw new InstantQuotePayloadTooLargeException("Uploaded file exceeds the maximum size.");
        }

        _hash.AppendData(buffer.Span[..read]);
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        await _source.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
