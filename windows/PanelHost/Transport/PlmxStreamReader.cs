using System.Buffers.Binary;
using System.Diagnostics;

namespace PanelHost.Transport;

/// <summary>
/// The single reader in front of the physical stream. It owns frame-aligned
/// reads and the resynchronization search, so no other code has to reason about
/// where a frame starts.
///
/// Resync scans a buffer with a vectorized search for the magic rather than
/// reading a byte at a time: the carrier's syscall-per-frame budget is the whole
/// point of the transport optimization and must not be spent here.
/// </summary>
internal sealed class PlmxStreamReader
{
    private const int DefaultBufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly PlmxResyncLimits _limits;
    private byte[] _buffer;
    private int _offset;
    private int _length;

    public PlmxStreamReader(Stream stream, PlmxResyncLimits? limits = null, int bufferBytes = DefaultBufferBytes)
    {
        _stream = stream;
        _limits = limits ?? PlmxResyncLimits.Default;
        // The buffer must hold a complete handshake candidate so the scanner can
        // validate header and payload together without re-reading.
        _buffer = new byte[Math.Max(bufferBytes, (PlmxFrame.HeaderSize + PlmxFrame.HelloPayloadSize) * 2)];
    }

    /// <summary>Bytes discarded by the most recent search.</summary>
    public long DiscardedBytes { get; private set; }

    public async ValueTask ReadExactlyAsync(Memory<byte> target, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < target.Length)
        {
            if (_length == 0) await FillAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(target.Length - written, _length);
            _buffer.AsSpan(_offset, count).CopyTo(target.Span[written..]);
            _offset += count;
            _length -= count;
            written += count;
        }
    }

    /// <summary>
    /// Reads the next frame header.
    ///
    /// A header that does not decode is deliberately left in the buffer. If the
    /// bytes were consumed, a resync starting from the next position could have
    /// eaten the head of the very handshake it needs to find - which is exactly
    /// the case where the previous session ended mid-frame.
    /// </summary>
    public async ValueTask<PlmxFrameHeader> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        while (_length < PlmxFrame.HeaderSize) await FillAsync(cancellationToken).ConfigureAwait(false);
        if (!PlmxFrame.TryDecodeHeader(_buffer.AsSpan(_offset, PlmxFrame.HeaderSize), out var header))
            throw new InvalidDataException("Malformed PLMX header.");
        _offset += PlmxFrame.HeaderSize;
        _length -= PlmxFrame.HeaderSize;
        return header;
    }

    /// <summary>
    /// Scans forward until a frame of <paramref name="expected"/> type decodes
    /// completely and <paramref name="accept"/> agrees, discarding everything
    /// before it. Everything after the match stays buffered, so the caller
    /// continues reading normal frames with no gap.
    ///
    /// This is what makes reconnect deterministic: the session does not need to
    /// know how many bytes the previous session left behind.
    /// </summary>
    public async ValueTask<PlmxHello> SearchHandshakeAsync(
        PlmxFrameType expected,
        Func<PlmxHello, bool> accept,
        Action<long>? onProgress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        DiscardedBytes = 0;
        var candidateSize = PlmxFrame.HeaderSize + PlmxFrame.HelloPayloadSize;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DiscardedBytes > _limits.MaximumBytes)
            {
                throw new PlmxResyncException(
                    $"Resync discarded {DiscardedBytes} bytes without finding a {expected} frame.",
                    DiscardedBytes);
            }
            if (Stopwatch.GetElapsedTime(started) > _limits.MaximumDuration)
            {
                throw new PlmxResyncException(
                    $"Resync exceeded {_limits.MaximumDuration.TotalSeconds:F0}s without finding a {expected} frame.",
                    DiscardedBytes);
            }

            // The buffer scan is a separate synchronous step because a span
            // cannot live across an await.
            var step = ScanBuffered(expected, accept, candidateSize, onProgress, out var hello);
            if (step == ScanResult.Matched) return hello;
            if (step == ScanResult.NeedMoreData) await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private enum ScanResult
    {
        NeedMoreData,
        Progressed,
        Matched,
    }

    private ScanResult ScanBuffered(
        PlmxFrameType expected,
        Func<PlmxHello, bool> accept,
        int candidateSize,
        Action<long>? onProgress,
        out PlmxHello hello)
    {
        hello = default;
        if (_length < candidateSize)
        {
            // Keep whatever could still be the head of a candidate; discard the
            // rest so a long run of rubbish cannot pin the buffer.
            var window = _buffer.AsSpan(_offset, _length);
            var partial = IndexOfMagic(window);
            if (partial > 0) Discard(partial, onProgress);
            return ScanResult.NeedMoreData;
        }

        var buffered = _buffer.AsSpan(_offset, _length);
        var found = IndexOfMagic(buffered);
        if (found < 0)
        {
            // Keep the last three bytes: the magic may straddle the boundary.
            var keep = Math.Min(MagicSize - 1, _length);
            Discard(_length - keep, onProgress);
            return ScanResult.NeedMoreData;
        }
        if (found > 0)
        {
            Discard(found, onProgress);
            return ScanResult.Progressed;
        }
        if (_length < candidateSize) return ScanResult.NeedMoreData;

        var candidate = _buffer.AsSpan(_offset, candidateSize);
        if (PlmxFrame.TryDecodeHeader(candidate, out var header) &&
            PlmxFrame.IsHandshakeFrame(header, candidate[PlmxFrame.HeaderSize..], expected, out hello) &&
            accept(hello))
        {
            _offset += candidateSize;
            _length -= candidateSize;
            return ScanResult.Matched;
        }

        // The magic matched but the frame did not. That is exactly the "magic
        // inside a stale payload" case, so step over this byte and keep looking
        // instead of trusting it.
        Discard(1, onProgress);
        return ScanResult.Progressed;
    }

    private const int MagicSize = 4;

    private static int IndexOfMagic(ReadOnlySpan<byte> window)
    {
        Span<byte> magic = stackalloc byte[MagicSize];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, PlmxFrame.Magic);
        return window.IndexOf(magic);
    }

    private void Discard(int count, Action<long>? onProgress)
    {
        if (count <= 0) return;
        _offset += count;
        _length -= count;
        DiscardedBytes += count;
        onProgress?.Invoke(DiscardedBytes);
    }

    private void Compact()
    {
        if (_offset == 0) return;
        if (_length > 0) _buffer.AsSpan(_offset, _length).CopyTo(_buffer);
        _offset = 0;
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        Compact();
        if (_length == _buffer.Length)
            throw new InvalidOperationException("The PLMX read buffer cannot grow further.");
        var read = await _stream
            .ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0) throw new EndOfStreamException("The physical transport ended.");
        _length += read;
    }
}
