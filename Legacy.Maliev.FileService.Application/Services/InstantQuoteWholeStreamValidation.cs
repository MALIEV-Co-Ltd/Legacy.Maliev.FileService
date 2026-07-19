using System.Diagnostics.CodeAnalysis;
using Legacy.Maliev.FileService.Application.Interfaces;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Applies bounded whole-stream validation to formats that require more than a prefix signature.</summary>
public static class InstantQuoteWholeStreamValidation
{
    /// <summary>Wraps glTF JSON content in a validating stream and leaves other formats unchanged.</summary>
    public static Stream Wrap(string extension, Stream source) =>
        string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase)
            ? new GltfValidationReadStream(source)
            : source;

    private sealed class GltfValidationReadStream(Stream source) : Stream
    {
        private readonly GltfJsonValidator validator = new();
        private bool completed;

        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count == 0)
            {
                return 0;
            }

            var read = source.Read(buffer, offset, count);
            ValidateRead(buffer.AsSpan(offset, read), read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            var read = await source.ReadAsync(buffer, cancellationToken);
            ValidateRead(buffer.Span[..read], read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void ValidateRead(ReadOnlySpan<byte> bytes, int read)
        {
            if (completed)
            {
                return;
            }

            try
            {
                if (read == 0)
                {
                    validator.Complete();
                    completed = true;
                    return;
                }

                validator.Append(bytes);
            }
            catch (InvalidDataException)
            {
                throw new InstantQuoteUnsafeContentException(
                    "Uploaded glTF content is not a complete document with a top-level asset object.");
            }
        }
    }

    private sealed class GltfJsonValidator
    {
        private const int MaximumDepth = 256;
        private static readonly byte[] TrueLiteral = "true"u8.ToArray();
        private static readonly byte[] FalseLiteral = "false"u8.ToArray();
        private static readonly byte[] NullLiteral = "null"u8.ToArray();
        private readonly Frame[] frames = new Frame[MaximumDepth];
        private int depth;
        private bool rootStarted;
        private bool rootComplete;
        private bool assetObjectFound;
        private TokenKind token;
        private bool stringIsProperty;
        private bool keyMismatch;
        private int keyPosition;
        private bool escape;
        private int unicodeDigits;
        private int unicodeValue;
        private int pendingHighSurrogate;
        private int utf8ContinuationBytes;
        private byte utf8NextMinimum = 0x80;
        private byte utf8NextMaximum = 0xbf;
        private NumberState numberState;
        private byte[]? literal;
        private int literalIndex;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                Process(value);
            }
        }

        public void Complete()
        {
            if (token == TokenKind.Number)
            {
                if (!IsCompleteNumber(numberState))
                {
                    Invalid();
                }
                token = TokenKind.None;
                ValueComplete();
            }
            else if (token != TokenKind.None)
            {
                Invalid();
            }

            if (!rootStarted || !rootComplete || depth != 0 || !assetObjectFound)
            {
                Invalid();
            }
        }

        private void Process(byte value)
        {
            var reprocess = true;
            while (reprocess)
            {
                reprocess = false;
                switch (token)
                {
                    case TokenKind.String:
                        ProcessString(value);
                        return;
                    case TokenKind.Number:
                        if (ProcessNumber(value))
                        {
                            return;
                        }
                        token = TokenKind.None;
                        ValueComplete();
                        reprocess = true;
                        continue;
                    case TokenKind.Literal:
                        ProcessLiteral(value);
                        return;
                }

                ProcessStructural(value);
            }
        }

        private void ProcessStructural(byte value)
        {
            if (IsWhitespace(value))
            {
                return;
            }
            if (rootComplete)
            {
                Invalid();
            }
            if (!rootStarted)
            {
                if (value != (byte)'{')
                {
                    Invalid();
                }
                rootStarted = true;
                Push(ContainerKind.Object);
                return;
            }
            if (depth == 0)
            {
                Invalid();
            }

            ref var frame = ref frames[depth - 1];
            switch (frame.Expectation)
            {
                case Expectation.ObjectPropertyOrEnd:
                    if (value == (byte)'}')
                    {
                        Close(ContainerKind.Object);
                    }
                    else
                    {
                        StartProperty(value);
                    }
                    return;
                case Expectation.ObjectProperty:
                    StartProperty(value);
                    return;
                case Expectation.ObjectColon:
                    if (value != (byte)':')
                    {
                        Invalid();
                    }
                    frame.Expectation = Expectation.ObjectValue;
                    return;
                case Expectation.ObjectValue:
                case Expectation.ArrayValue:
                    StartValue(value);
                    return;
                case Expectation.ObjectCommaOrEnd:
                    if (value == (byte)',')
                    {
                        frame.Expectation = Expectation.ObjectProperty;
                    }
                    else if (value == (byte)'}')
                    {
                        Close(ContainerKind.Object);
                    }
                    else
                    {
                        Invalid();
                    }
                    return;
                case Expectation.ArrayValueOrEnd:
                    if (value == (byte)']')
                    {
                        Close(ContainerKind.Array);
                    }
                    else
                    {
                        StartValue(value);
                    }
                    return;
                case Expectation.ArrayCommaOrEnd:
                    if (value == (byte)',')
                    {
                        frame.Expectation = Expectation.ArrayValue;
                    }
                    else if (value == (byte)']')
                    {
                        Close(ContainerKind.Array);
                    }
                    else
                    {
                        Invalid();
                    }
                    return;
                default:
                    Invalid();
                    return;
            }
        }

        private void StartProperty(byte value)
        {
            if (value != (byte)'\"')
            {
                Invalid();
            }
            StartString(isProperty: true);
        }

        private void StartValue(byte value)
        {
            var topLevelAsset = depth == 1 && frames[0].Kind == ContainerKind.Object &&
                frames[0].CurrentPropertyIsAsset;
            if (topLevelAsset && value != (byte)'{')
            {
                Invalid();
            }

            switch (value)
            {
                case (byte)'{':
                    if (topLevelAsset)
                    {
                        assetObjectFound = true;
                    }
                    Push(ContainerKind.Object);
                    break;
                case (byte)'[':
                    Push(ContainerKind.Array);
                    break;
                case (byte)'\"':
                    StartString(isProperty: false);
                    break;
                case (byte)'t':
                    StartLiteral(TrueLiteral);
                    break;
                case (byte)'f':
                    StartLiteral(FalseLiteral);
                    break;
                case (byte)'n':
                    StartLiteral(NullLiteral);
                    break;
                case (byte)'-':
                    token = TokenKind.Number;
                    numberState = NumberState.Minus;
                    break;
                case >= (byte)'0' and <= (byte)'9':
                    token = TokenKind.Number;
                    numberState = value == (byte)'0' ? NumberState.Zero : NumberState.Integer;
                    break;
                default:
                    Invalid();
                    break;
            }
        }

        private void StartString(bool isProperty)
        {
            token = TokenKind.String;
            stringIsProperty = isProperty;
            keyMismatch = false;
            keyPosition = 0;
            escape = false;
            unicodeDigits = 0;
            unicodeValue = 0;
            pendingHighSurrogate = 0;
            utf8ContinuationBytes = 0;
        }

        private void ProcessString(byte value)
        {
            if (unicodeDigits > 0)
            {
                var digit = HexValue(value);
                if (digit < 0)
                {
                    Invalid();
                }
                unicodeValue = (unicodeValue << 4) | digit;
                unicodeDigits--;
                if (unicodeDigits == 0)
                {
                    ProcessEscapedCodeUnit(unicodeValue);
                }
                return;
            }
            if (escape)
            {
                escape = false;
                if (pendingHighSurrogate != 0 && value != (byte)'u')
                {
                    Invalid();
                }
                switch (value)
                {
                    case (byte)'u':
                        unicodeDigits = 4;
                        unicodeValue = 0;
                        return;
                    case (byte)'\"':
                    case (byte)'\\':
                    case (byte)'/':
                        FeedKeyCharacter(value);
                        return;
                    case (byte)'b':
                        FeedKeyCharacter('\b');
                        return;
                    case (byte)'f':
                        FeedKeyCharacter('\f');
                        return;
                    case (byte)'n':
                        FeedKeyCharacter('\n');
                        return;
                    case (byte)'r':
                        FeedKeyCharacter('\r');
                        return;
                    case (byte)'t':
                        FeedKeyCharacter('\t');
                        return;
                    default:
                        Invalid();
                        return;
                }
            }
            if (utf8ContinuationBytes > 0)
            {
                if (value < utf8NextMinimum || value > utf8NextMaximum)
                {
                    Invalid();
                }
                utf8ContinuationBytes--;
                utf8NextMinimum = 0x80;
                utf8NextMaximum = 0xbf;
                keyMismatch = keyMismatch || stringIsProperty;
                return;
            }
            if (pendingHighSurrogate != 0 && value != (byte)'\\')
            {
                Invalid();
            }
            if (value == (byte)'\"')
            {
                if (pendingHighSurrogate != 0)
                {
                    Invalid();
                }
                token = TokenKind.None;
                if (stringIsProperty)
                {
                    ref var frame = ref frames[depth - 1];
                    frame.CurrentPropertyIsAsset = !keyMismatch && keyPosition == 5;
                    frame.Expectation = Expectation.ObjectColon;
                }
                else
                {
                    ValueComplete();
                }
                return;
            }
            if (value == (byte)'\\')
            {
                escape = true;
                return;
            }
            if (value < 0x20)
            {
                Invalid();
            }
            if (value < 0x80)
            {
                FeedKeyCharacter(value);
                return;
            }

            keyMismatch = keyMismatch || stringIsProperty;
            switch (value)
            {
                case >= 0xc2 and <= 0xdf:
                    utf8ContinuationBytes = 1;
                    break;
                case 0xe0:
                    utf8ContinuationBytes = 2;
                    utf8NextMinimum = 0xa0;
                    break;
                case >= 0xe1 and <= 0xec:
                case >= 0xee and <= 0xef:
                    utf8ContinuationBytes = 2;
                    break;
                case 0xed:
                    utf8ContinuationBytes = 2;
                    utf8NextMaximum = 0x9f;
                    break;
                case 0xf0:
                    utf8ContinuationBytes = 3;
                    utf8NextMinimum = 0x90;
                    break;
                case >= 0xf1 and <= 0xf3:
                    utf8ContinuationBytes = 3;
                    break;
                case 0xf4:
                    utf8ContinuationBytes = 3;
                    utf8NextMaximum = 0x8f;
                    break;
                default:
                    Invalid();
                    break;
            }
        }

        private void ProcessEscapedCodeUnit(int codeUnit)
        {
            if (codeUnit is >= 0xd800 and <= 0xdbff)
            {
                if (pendingHighSurrogate != 0)
                {
                    Invalid();
                }
                pendingHighSurrogate = codeUnit;
                keyMismatch = keyMismatch || stringIsProperty;
                return;
            }
            if (codeUnit is >= 0xdc00 and <= 0xdfff)
            {
                if (pendingHighSurrogate == 0)
                {
                    Invalid();
                }
                pendingHighSurrogate = 0;
                keyMismatch = keyMismatch || stringIsProperty;
                return;
            }
            if (pendingHighSurrogate != 0)
            {
                Invalid();
            }
            FeedKeyCharacter(codeUnit);
        }

        private void FeedKeyCharacter(int value)
        {
            if (!stringIsProperty || keyMismatch)
            {
                return;
            }
            const string asset = "asset";
            if (keyPosition >= asset.Length || value != asset[keyPosition])
            {
                keyMismatch = true;
                return;
            }
            keyPosition++;
        }

        private bool ProcessNumber(byte value)
        {
            switch (numberState)
            {
                case NumberState.Minus:
                    if (value == (byte)'0')
                    {
                        numberState = NumberState.Zero;
                        return true;
                    }
                    if (value is >= (byte)'1' and <= (byte)'9')
                    {
                        numberState = NumberState.Integer;
                        return true;
                    }
                    Invalid();
                    return true;
                case NumberState.Zero:
                    if (value == (byte)'.')
                    {
                        numberState = NumberState.Dot;
                        return true;
                    }
                    if (value is (byte)'e' or (byte)'E')
                    {
                        numberState = NumberState.Exponent;
                        return true;
                    }
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        Invalid();
                    }
                    return false;
                case NumberState.Integer:
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        return true;
                    }
                    if (value == (byte)'.')
                    {
                        numberState = NumberState.Dot;
                        return true;
                    }
                    if (value is (byte)'e' or (byte)'E')
                    {
                        numberState = NumberState.Exponent;
                        return true;
                    }
                    return false;
                case NumberState.Dot:
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        numberState = NumberState.Fraction;
                        return true;
                    }
                    Invalid();
                    return true;
                case NumberState.Fraction:
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        return true;
                    }
                    if (value is (byte)'e' or (byte)'E')
                    {
                        numberState = NumberState.Exponent;
                        return true;
                    }
                    return false;
                case NumberState.Exponent:
                    if (value is (byte)'+' or (byte)'-')
                    {
                        numberState = NumberState.ExponentSign;
                        return true;
                    }
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        numberState = NumberState.ExponentDigits;
                        return true;
                    }
                    Invalid();
                    return true;
                case NumberState.ExponentSign:
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        numberState = NumberState.ExponentDigits;
                        return true;
                    }
                    Invalid();
                    return true;
                case NumberState.ExponentDigits:
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        return true;
                    }
                    return false;
                default:
                    Invalid();
                    return true;
            }
        }

        private void StartLiteral(byte[] expected)
        {
            token = TokenKind.Literal;
            literal = expected;
            literalIndex = 1;
        }

        private void ProcessLiteral(byte value)
        {
            if (literal is null || literalIndex >= literal.Length || value != literal[literalIndex])
            {
                Invalid();
            }
            literalIndex++;
            if (literalIndex == literal.Length)
            {
                token = TokenKind.None;
                literal = null;
                ValueComplete();
            }
        }

        private void Push(ContainerKind kind)
        {
            if (depth == MaximumDepth)
            {
                Invalid();
            }
            frames[depth++] = new Frame
            {
                Kind = kind,
                Expectation = kind == ContainerKind.Object
                    ? Expectation.ObjectPropertyOrEnd
                    : Expectation.ArrayValueOrEnd,
            };
        }

        private void Close(ContainerKind kind)
        {
            if (depth == 0 || frames[depth - 1].Kind != kind)
            {
                Invalid();
            }
            depth--;
            if (depth == 0)
            {
                rootComplete = true;
            }
            else
            {
                ValueComplete();
            }
        }

        private void ValueComplete()
        {
            if (depth == 0)
            {
                rootComplete = true;
                return;
            }
            ref var frame = ref frames[depth - 1];
            frame.Expectation = frame.Kind == ContainerKind.Object
                ? Expectation.ObjectCommaOrEnd
                : Expectation.ArrayCommaOrEnd;
            frame.CurrentPropertyIsAsset = false;
        }

        private static bool IsCompleteNumber(NumberState state) =>
            state is NumberState.Zero or NumberState.Integer or NumberState.Fraction or NumberState.ExponentDigits;

        private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        private static int HexValue(byte value) => value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
            >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
            _ => -1,
        };

        [DoesNotReturn]
        private static void Invalid() => throw new InvalidDataException("Invalid streamed glTF JSON.");

        private enum ContainerKind { Object, Array }
        private enum TokenKind { None, String, Number, Literal }
        private enum NumberState { Minus, Zero, Integer, Dot, Fraction, Exponent, ExponentSign, ExponentDigits }
        private enum Expectation
        {
            ObjectPropertyOrEnd,
            ObjectProperty,
            ObjectColon,
            ObjectValue,
            ObjectCommaOrEnd,
            ArrayValueOrEnd,
            ArrayValue,
            ArrayCommaOrEnd,
        }

        private struct Frame
        {
            public ContainerKind Kind;
            public Expectation Expectation;
            public bool CurrentPropertyIsAsset;
        }
    }
}
