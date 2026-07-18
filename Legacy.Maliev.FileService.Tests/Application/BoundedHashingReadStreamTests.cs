using System.Security.Cryptography;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class BoundedHashingReadStreamTests
{
    [Fact]
    public async Task ReadAsync_ExactLimit_IsAcceptedWithoutLengthOrSeek()
    {
        await using var stream = new BoundedHashingReadStream(
            new GeneratedNonSeekableStream(200L * 1024 * 1024),
            200L * 1024 * 1024);

        await DrainAsync(stream);

        Assert.Equal(200L * 1024 * 1024, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_LimitPlusOne_ThrowsImmediately()
    {
        var source = new GeneratedNonSeekableStream(200);
        await using var stream = new BoundedHashingReadStream(source, maximumBytes: 10, maximumReadSize: 64);

        await Assert.ThrowsAsync<InstantQuotePayloadTooLargeException>(() => DrainAsync(stream));
        Assert.Equal(11, stream.BytesRead);
        Assert.Equal(11, source.TotalBytesRead);
        Assert.Equal(11, source.LargestRequestedRead);
    }

    [Fact]
    public async Task ReadAsync_EndOfStream_ExposesIncrementalLowercaseSha256()
    {
        var bytes = "incremental hash"u8.ToArray();
        await using var stream = new BoundedHashingReadStream(new GeneratedNonSeekableStream(bytes));

        await DrainAsync(stream, 3);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), stream.Sha256);
    }

    [Fact]
    public async Task ReadAsync_LargeCallerBuffer_BoundsUnderlyingReadSize()
    {
        var source = new GeneratedNonSeekableStream(200_000);
        await using var stream = new BoundedHashingReadStream(source, maximumBytes: 300_000, maximumReadSize: 8192);

        await DrainAsync(stream, 100_000);

        Assert.InRange(source.LargestRequestedRead, 1, 8192);
    }

    [Fact]
    public async Task ReadAsync_CanceledToken_PropagatesCancellation()
    {
        await using var stream = new BoundedHashingReadStream(new GeneratedNonSeekableStream(10));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.CopyToAsync(Stream.Null, source.Token));
    }

    [Fact]
    public async Task ReadAsync_EmptyDestination_DoesNotMarkSourceComplete()
    {
        await using var stream = new BoundedHashingReadStream(new GeneratedNonSeekableStream(1));

        var read = await stream.ReadAsync(Memory<byte>.Empty);

        Assert.Equal(0, read);
        Assert.False(stream.IsComplete);
        await DrainAsync(stream);
        Assert.True(stream.IsComplete);
    }

    [Fact]
    public async Task ReadAsync_EmptySource_FinalizesEmptySha256()
    {
        await using var stream = new BoundedHashingReadStream(new GeneratedNonSeekableStream(0));

        var read = await stream.ReadAsync(new byte[1]);

        Assert.Equal(0, read);
        Assert.True(stream.IsComplete);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), stream.Sha256);
    }

    private static async Task DrainAsync(Stream stream, int bufferSize = 64 * 1024)
    {
        var buffer = new byte[bufferSize];
        while (await stream.ReadAsync(buffer) != 0)
        {
        }
    }

    private sealed class GeneratedNonSeekableStream : Stream
    {
        private readonly byte[]? _bytes;
        private long _remaining;
        private long _position;

        public GeneratedNonSeekableStream(long length) => _remaining = length;

        public GeneratedNonSeekableStream(byte[] bytes)
        {
            _bytes = bytes;
            _remaining = bytes.Length;
        }

        public int LargestRequestedRead { get; private set; }
        public long TotalBytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length);
            var count = (int)Math.Min(buffer.Length, _remaining);
            if (count == 0)
            {
                return ValueTask.FromResult(0);
            }

            if (_bytes is null)
            {
                buffer.Span[..count].Fill(0x5a);
            }
            else
            {
                _bytes.AsSpan((int)_position, count).CopyTo(buffer.Span);
            }

            _position += count;
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
