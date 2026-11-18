using System.Text;

namespace PanelOS.Windows.Protocol;

public static class JsonLineProtocolLimits
{
    // A 512 KiB PNG expands to roughly 683 KiB as Base64. One MiB leaves
    // room for its envelope while keeping every control allocation bounded.
    public const int MaxControlMessageBytes = 1024 * 1024;
}

public sealed class JsonLineProtocolException : IOException
{
    public JsonLineProtocolException(string message)
        : base(message)
    {
    }

    public JsonLineProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Incremental LF reader with a strict UTF-8 byte budget.</summary>
public sealed class BoundedUtf8LineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly int _maximumBytes;
    private readonly byte[] _readBuffer;
    private readonly MemoryStream _line = new();
    private int _bufferOffset;
    private int _bufferCount;

    public BoundedUtf8LineReader(
        Stream stream,
        int maximumBytes = JsonLineProtocolLimits.MaxControlMessageBytes,
        int bufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _stream = stream;
        _maximumBytes = maximumBytes;
        _readBuffer = new byte[bufferSize];
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_bufferOffset < _bufferCount)
            {
                var newline = Array.IndexOf(
                    _readBuffer,
                    (byte)'\n',
                    _bufferOffset,
                    _bufferCount - _bufferOffset);
                var segmentLength = newline >= 0
                    ? newline - _bufferOffset
                    : _bufferCount - _bufferOffset;
                if (_line.Length + segmentLength > _maximumBytes)
                {
                    throw new JsonLineProtocolException(
                        $"JSONL message exceeds {_maximumBytes} UTF-8 bytes.");
                }

                if (segmentLength > 0)
                {
                    _line.Write(_readBuffer, _bufferOffset, segmentLength);
                }
                _bufferOffset += segmentLength;

                if (newline >= 0)
                {
                    _bufferOffset++;
                    return DecodeAndReset();
                }
            }

            _bufferOffset = 0;
            _bufferCount = await _stream.ReadAsync(
                    _readBuffer.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (_bufferCount != 0)
            {
                continue;
            }

            if (_line.Length == 0)
            {
                return null;
            }

            _line.SetLength(0);
            throw new EndOfStreamException("Peer disconnected during a JSONL message.");
        }
    }

    private string DecodeAndReset()
    {
        try
        {
            var length = checked((int)_line.Length);
            var bytes = _line.GetBuffer().AsSpan(0, length);
            if (bytes.EndsWith("\r"u8))
            {
                bytes = bytes[..^1];
            }
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonLineProtocolException(
                "JSONL message contains invalid UTF-8.",
                exception);
        }
        finally
        {
            _line.SetLength(0);
        }
    }
}
