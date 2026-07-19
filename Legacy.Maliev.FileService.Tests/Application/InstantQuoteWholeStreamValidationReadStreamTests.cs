using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class InstantQuoteWholeStreamValidationReadStreamTests
{
    [Fact]
    public async Task Read_GltfAssetObjectIsFirst_AcceptsCompleteDocument()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"scene\":0}");

        await DrainAsync(InstantQuoteWholeStreamValidation.Wrap(".gltf", new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Read_GltfAssetObjectAfterFourKiB_AcceptsCompleteDocumentWithoutPrefixAssumption()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "{\"extras\":{\"padding\":\"" + new string('a', 5000) + "\"},\"asset\":{\"version\":\"2.0\"}}");

        await DrainAsync(InstantQuoteWholeStreamValidation.Wrap(".gltf", new ChunkedNonSeekableStream(bytes, 37)));
    }

    [Fact]
    public async Task Read_GltfChunkSplitsInsideEscapedAssetKey_AcceptsCompleteDocument()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"\\u0061sset\":{\"version\":\"2.0\"}}");

        await DrainAsync(InstantQuoteWholeStreamValidation.Wrap(".gltf", new ChunkedNonSeekableStream(bytes, 1)));
    }

    [Fact]
    public async Task Read_GltfCompleteJsonGrammarAcrossSingleByteChunks_AcceptsDocument()
    {
        var document =
            "{\"asset\":{\"version\":\"2.0\",\"generator\":\"MALIEV ไทย 🚀\"}," +
            "\"extras\":{\"escaped\":\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"," +
            "\"values\":[true,false,null,-0,12,1.25,1e+3,-2E-2],\"empty\":[]}}";
        var bytes = Encoding.UTF8.GetBytes(document);

        var read = await DrainAsync(
            InstantQuoteWholeStreamValidation.Wrap(".gltf", new ChunkedNonSeekableStream(bytes, 1)));

        Assert.Equal(bytes.Length, read);
    }

    [Fact]
    public void Read_GltfThroughSynchronousStream_ValidatesCompleteDocument()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"mesh\":0}]}");
        using var stream = InstantQuoteWholeStreamValidation.Wrap(".gltf", new MemoryStream(bytes));
        var buffer = new byte[7];
        var total = 0;

        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        Assert.Equal(bytes.Length, total);
    }

    [Theory]
    [InlineData("{\"scene\":0}")]
    [InlineData("{\"asset\":null}")]
    [InlineData("{\"asset\":[]}")]
    public async Task Read_GltfMissingAssetObject_RejectsCompleteDocument(string document)
    {
        var stream = InstantQuoteWholeStreamValidation.Wrap(
            ".gltf", new MemoryStream(Encoding.UTF8.GetBytes(document)));

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => DrainAsync(stream));
    }

    [Theory]
    [InlineData("{\"asset\":{\"version\":\"2.0\"}} trailing")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},,}")]
    [InlineData("{\"asset\":{\"version\":\"\\uD800\"}}")]
    [InlineData("{\"asset\":{\"version\":\"\\uD800x\\uDC00\"}}")]
    public async Task Read_GltfMalformedOrUnbalancedTail_RejectsCompleteDocument(string document)
    {
        var stream = InstantQuoteWholeStreamValidation.Wrap(
            ".gltf", new ChunkedNonSeekableStream(Encoding.UTF8.GetBytes(document), 2));

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => DrainAsync(stream));
    }

    [Theory]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"value\":-}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"value\":01}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"value\":1.}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"value\":1e}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"value\":1e+}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"value\":truX}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"items\":[1 2]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"items\":[1,]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"items\":{]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"bad\\q\":1}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"bad\\u12xz\":1}")]
    public async Task Read_GltfMalformedJsonTokens_RejectsFailClosed(string document)
    {
        var stream = InstantQuoteWholeStreamValidation.Wrap(
            ".gltf", new ChunkedNonSeekableStream(Encoding.UTF8.GetBytes(document), 1));

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => DrainAsync(stream, bufferSize: 3));
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8Documents))]
    public async Task Read_GltfInvalidUtf8_RejectsFailClosed(byte[] document)
    {
        var stream = InstantQuoteWholeStreamValidation.Wrap(
            ".gltf", new ChunkedNonSeekableStream(document, 1));

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => DrainAsync(stream, bufferSize: 2));
    }

    [Fact]
    public async Task Read_GltfNestingAboveBound_RejectsBeforeResourceGrowth()
    {
        var document = "{\"asset\":{\"version\":\"2.0\"},\"extras\":" +
            new string('[', 257) + "0" + new string(']', 257) + "}";
        var stream = InstantQuoteWholeStreamValidation.Wrap(
            ".gltf", new ChunkedNonSeekableStream(Encoding.UTF8.GetBytes(document), 17));

        await Assert.ThrowsAsync<InstantQuoteUnsafeContentException>(() => DrainAsync(stream));
    }

    [Fact]
    public async Task Read_LargeNonSeekableGltf_UsesCallerBoundedReadsAndDoesNotBufferLongTokens()
    {
        const int paddingBytes = 8 * 1024 * 1024;
        var source = new GeneratedGltfStream(paddingBytes);
        var validated = InstantQuoteWholeStreamValidation.Wrap(".gltf", source);

        var read = await DrainAsync(validated, bufferSize: 113);

        Assert.Equal(source.DocumentLength, read);
        Assert.InRange(source.MaximumRequestedReadLength, 1, 113);
        Assert.False(validated.CanSeek);
    }

    [Fact]
    public void Wrap_NonGltfFormat_ReturnsOriginalStreamUnaffected()
    {
        var source = new MemoryStream("solid part"u8.ToArray());

        var wrapped = InstantQuoteWholeStreamValidation.Wrap(".stl", source);

        Assert.Same(source, wrapped);
    }

    public static TheoryData<byte[]> InvalidUtf8Documents()
    {
        static byte[] WithInvalidString(params byte[] invalidBytes) =>
        [
            .. "{\"asset\":{\"version\":\"2.0\"},\"extras\":\""u8.ToArray(),
            .. invalidBytes,
            .. "\"}"u8.ToArray(),
        ];

        return new TheoryData<byte[]>
        {
            WithInvalidString(0x01),
            WithInvalidString(0x80),
            WithInvalidString(0xc0, 0x80),
            WithInvalidString(0xe0, 0x80, 0x80),
            WithInvalidString(0xed, 0xa0, 0x80),
            WithInvalidString(0xf0, 0x80, 0x80, 0x80),
            WithInvalidString(0xf4, 0x90, 0x80, 0x80),
            WithInvalidString(0xf5, 0x80, 0x80, 0x80),
        };
    }

    private static async Task<long> DrainAsync(Stream stream, int bufferSize = 257)
    {
        await using (stream)
        {
            var buffer = new byte[bufferSize];
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return total;
                }
                total += read;
            }
        }
    }

    private sealed class ChunkedNonSeekableStream(byte[] bytes, int maximumChunkSize) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> destination)
        {
            var count = Math.Min(Math.Min(destination.Length, maximumChunkSize), bytes.Length - position);
            bytes.AsSpan(position, count).CopyTo(destination);
            position += count;
            return count;
        }
    }

    private sealed class GeneratedGltfStream(int paddingBytes) : Stream
    {
        private static readonly byte[] Prefix = "{\"extras\":\""u8.ToArray();
        private static readonly byte[] Suffix = "\",\"asset\":{\"version\":\"2.0\"}}"u8.ToArray();
        private long position;
        public long DocumentLength => Prefix.Length + (long)paddingBytes + Suffix.Length;
        public int MaximumRequestedReadLength { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> destination)
        {
            MaximumRequestedReadLength = Math.Max(MaximumRequestedReadLength, destination.Length);
            var remaining = DocumentLength - position;
            var count = checked((int)Math.Min(destination.Length, remaining));
            for (var index = 0; index < count; index++)
            {
                var absolute = position + index;
                destination[index] = absolute < Prefix.Length
                    ? Prefix[checked((int)absolute)]
                    : absolute < Prefix.Length + paddingBytes
                        ? (byte)'a'
                        : Suffix[checked((int)(absolute - Prefix.Length - paddingBytes))];
            }
            position += count;
            return count;
        }
    }
}
