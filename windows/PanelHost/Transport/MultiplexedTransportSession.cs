using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace PanelHost.Transport;

/// <summary>Which end of the carrier owns session identity.</summary>
internal enum PlmxRole
{
    /// <summary>Generates the session id and sends HELLO. PanelHost is always the host.</summary>
    Host,

    /// <summary>Adopts the host's session id and answers HELLO_ACK.</summary>
    Accessory,
}

/// <summary>
/// Carries the logical PanelOS byte streams over one full-duplex physical stream
/// using PLMX v2.
///
/// Control writes are always selected before video writes. The video queue holds
/// at most one pending write, so the existing capture mailbox remains the only
/// place where frames accumulate (and it already uses latest-frame-wins).
///
/// Three costs are deliberately engineered out of the steady state and must stay
/// out. A logical write is fragmented by slicing, never by copying, because the
/// caller stays parked on the fragment's completion and therefore still owns its
/// buffer. A frame that fits the scratch buffer is pushed to the wire as a single
/// physical write. Received payloads come from a pool with one explicit owner.
///
/// What v2 adds is identity. Every frame carries a tag derived from a 128-bit
/// session nonce, and no channel frame is interpreted until the HELLO/HELLO_ACK
/// exchange has bound both ends to the same nonce. That is what lets a session
/// opened over a pipe still holding the previous session's bytes resynchronize
/// deterministically instead of decoding those bytes as its own.
/// </summary>
internal sealed class MultiplexedTransportSession : IAsyncDisposable
{
    private readonly Stream _physicalStream;
    private readonly PlmxStreamReader _reader;
    private readonly PlmxResyncLimits _resyncLimits;
    private readonly PanelTransportMetrics _metrics;
    private readonly TransportIoProfile _profile;
    private readonly PlmxRole _role;
    private readonly TextWriter _log;
    private readonly int _fragmentBytes;
    private readonly CarrierLimits _limits;
    private readonly CarrierBudget _budget;
    private readonly CarrierOpenRate _openRate;
    private readonly byte[] _frameScratch;
    private readonly byte[] _headerScratch = new byte[PlmxFrame.HeaderSize];
    private readonly uint[] _txSequences = new uint[MaximumChannelId + 1];
    private readonly uint[] _rxSequences = new uint[MaximumChannelId + 1];
    private readonly bool[] _rxSeen = new bool[MaximumChannelId + 1];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<OutboundWrite> _controlWrites = Channel.CreateUnbounded<OutboundWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<OutboundWrite> _videoWrites = Channel.CreateBounded<OutboundWrite>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Dictionary<PanelChannelId, LogicalChannelStream> _channels = new();
    private readonly HashSet<LogicalChannelStream> _wifiStreams = [];
    private readonly Dictionary<PanelChannelId, Channel<LogicalChannelStream>> _acceptedChannels;
    private readonly object _channelLock = new();
    private readonly SemaphoreSlim _writeReady = new(0);
    private Task? _readerTask;
    private Task? _writerTask;
    private int _controlDepth;
    private int _videoDepth;
    private int _started;
    private int _failed;
    private PlmxSessionState _state = PlmxSessionState.Disconnected;

    private const byte MaximumChannelId = (byte)PanelChannelId.Audio;

    public MultiplexedTransportSession(
        Stream physicalStream,
        PlmxRole role = PlmxRole.Host,
        PanelTransportMetrics? metrics = null,
        int? fragmentBytes = null,
        TransportIoProfile? profile = null,
        PlmxResyncLimits? resyncLimits = null,
        TextWriter? log = null,
        CarrierLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(physicalStream);
        if (!physicalStream.CanRead || !physicalStream.CanWrite)
            throw new ArgumentException("The physical transport must be a full-duplex stream.", nameof(physicalStream));
        _physicalStream = physicalStream;
        _role = role;
        _openRate = new(maximum: role == PlmxRole.Host ? 16 : 12);
        _metrics = metrics ?? new PanelTransportMetrics();
        _profile = profile ?? _metrics.Io;
        _log = log ?? TextWriter.Null;
        _resyncLimits = resyncLimits ?? PlmxResyncLimits.Default;
        _limits = limits ?? CarrierLimits.Cable;
        _budget = new CarrierBudget(_limits);
        _reader = new PlmxStreamReader(physicalStream, _resyncLimits);
        _fragmentBytes = MultiplexFrame.SanitizeFragmentBytes(fragmentBytes ?? MultiplexFrame.ConfiguredFragmentBytes);
        if (_limits.IsWifi) _fragmentBytes = Math.Min(_fragmentBytes, CarrierLimits.WifiFrameBytes);
        _frameScratch = GC.AllocateUninitializedArray<byte>(PlmxFrame.HeaderSize + _fragmentBytes);
        _acceptedChannels = new[] { PanelChannelId.Control, PanelChannelId.Video, PanelChannelId.Assets, PanelChannelId.Audio }
            .ToDictionary(
                channel => channel,
                _ => _limits.IsWifi ? Channel.CreateBounded<LogicalChannelStream>(1) :
                    Channel.CreateUnbounded<LogicalChannelStream>(
                        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true }));
    }

    public PanelTransportMetrics Metrics => _metrics;
    public int FragmentBytes => _fragmentBytes;
    internal long CarrierLiveBytes => _budget.LiveBytes;
    public PlmxSessionId SessionId { get; private set; }
    public PlmxSessionState State => _state;
    public PanelTransportCapabilities Capabilities =>
        PanelTransportCapabilities.Multiplexing |
        PanelTransportCapabilities.ControlPriority |
        PanelTransportCapabilities.VideoBackpressure |
        PanelTransportCapabilities.SessionIdentity |
        PanelTransportCapabilities.Resync;

    public async ValueTask<Stream> OpenChannelAsync(PanelChannelId channel, CancellationToken cancellationToken)
    {
        if (!_acceptedChannels.ContainsKey(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        LogicalChannelStream stream;
        lock (_channelLock)
        {
            _limits.Validate(channel, 0);
            if (_limits.IsWifi)
            {
                if (_channels.ContainsKey(channel)) throw new InvalidDataException("Duplicate WiFi OPEN.");
                _openRate.Record(channel);
            }
            if (_channels.ContainsKey(channel)) throw new InvalidOperationException($"Channel {channel} is already open.");
            stream = new LogicalChannelStream(this, channel);
            _channels[channel] = stream;
        }
        await EnqueueAsync(channel, ReadOnlyMemory<byte>.Empty, PlmxFrameType.Open, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    public async ValueTask<Stream> AcceptChannelAsync(PanelChannelId channel, CancellationToken cancellationToken)
    {
        if (!_acceptedChannels.TryGetValue(channel, out var accepted)) throw new ArgumentOutOfRangeException(nameof(channel));
        return await accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes the PLMX v2 handshake before any loop starts, so the reader
    /// never has to distinguish a handshake from channel traffic and a stale
    /// stream is resolved once, up front.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The multiplexed session was already started.");
        _metrics.SessionStarted();
        _state = PlmxSessionState.OpeningPhysicalTransport;
        // The resync limits bound discarded bytes and elapsed time, but both are
        // only evaluated between reads. A peer that sends nothing at all leaves
        // the read blocked and the bound unreachable: the accessory that has not
        // opened its endpoint yet never sees the single HELLO, and both sides
        // then wait forever with no error, no reconnect and no metric moving.
        // Cancelling the handshake is what makes the documented bound hold - the
        // carrier reconnects with backoff, and by then the peer is listening.
        using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeDeadline.CancelAfter(_resyncLimits.MaximumDuration);
        try
        {
            if (_role == PlmxRole.Host) await HostHandshakeAsync(handshakeDeadline.Token).ConfigureAwait(false);
            else await AccessoryHandshakeAsync(handshakeDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _metrics.HandshakeFailed();
            _state = PlmxSessionState.Closing;
            // Reported as a resync failure so the carrier's existing retry path
            // handles it; a bare OperationCanceledException would read as a
            // caller-requested shutdown and skip the reconnect entirely.
            throw new PlmxResyncException(
                $"Handshake found no {(_role == PlmxRole.Host ? PlmxFrameType.HelloAck : PlmxFrameType.Hello)} " +
                $"within {_resyncLimits.MaximumDuration.TotalSeconds:F0}s.",
                _reader.DiscardedBytes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _metrics.HandshakeFailed();
            _state = PlmxSessionState.Closing;
            throw;
        }
        _state = PlmxSessionState.Established;
        _log.WriteLine(
            $"[PLMX] session established role={_role} session={SessionId.Prefix} " +
            $"version={PlmxFrame.Version} fragment={_fragmentBytes}B");
        _readerTask = ReaderLoopAsync(_lifetime.Token);
        _writerTask = WriterLoopAsync(_lifetime.Token);
    }

    private async Task HostHandshakeAsync(CancellationToken cancellationToken)
    {
        SessionId = PlmxSessionId.Create();
        _state = PlmxSessionState.Handshaking;
        await WriteHandshakeAsync(PlmxFrameType.Hello, cancellationToken).ConfigureAwait(false);
        _state = PlmxSessionState.SearchingForHandshake;
        var expected = SessionId;
        var acknowledgement = await SearchAsync(
            PlmxFrameType.HelloAck,
            hello => hello.SessionId == expected,
            cancellationToken).ConfigureAwait(false);
        ValidateCapabilities(acknowledgement);
    }

    private async Task AccessoryHandshakeAsync(CancellationToken cancellationToken)
    {
        _state = PlmxSessionState.SearchingForHandshake;
        // Any valid HELLO is adopted. A bulk pipe is FIFO, so a stale HELLO from
        // the previous session always precedes the current one; adopting the
        // newest match is therefore both safe and self-correcting.
        var hello = await SearchAsync(PlmxFrameType.Hello, _ => true, cancellationToken).ConfigureAwait(false);
        ValidateCapabilities(hello);
        SessionId = hello.SessionId;
        _state = PlmxSessionState.Handshaking;
        await WriteHandshakeAsync(PlmxFrameType.HelloAck, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlmxHello> SearchAsync(
        PlmxFrameType expected,
        Func<PlmxHello, bool> accept,
        CancellationToken cancellationToken)
    {
        var rejected = 0;
        var hello = await _reader.SearchHandshakeAsync(
            expected,
            candidate =>
            {
                if (accept(candidate)) return true;
                // A well-formed handshake for a different session: the previous
                // session's own greeting still sitting in the pipe.
                rejected++;
                _metrics.StaleFrameDropped();
                return false;
            },
            onProgress: null,
            cancellationToken).ConfigureAwait(false);
        if (_reader.DiscardedBytes > 0 || rejected > 0)
        {
            _metrics.Resynchronized(_reader.DiscardedBytes);
            _log.WriteLine(
                $"[PLMX] state=SearchingForHandshake discarded={_reader.DiscardedBytes} bytes " +
                $"staleHandshakes={rejected} foundMagic=true version={PlmxFrame.Version} " +
                $"session={hello.SessionId.Prefix}");
        }
        return hello;
    }

    private void ValidateCapabilities(PlmxHello hello)
    {
        if ((hello.Capabilities & PanelTransportCapabilities.Multiplexing) == 0)
            throw new InvalidDataException("Peer does not support multiplexing.");
    }

    private async Task WriteHandshakeAsync(PlmxFrameType type, CancellationToken cancellationToken)
    {
        var payload = PlmxFrame.EncodeHello(Capabilities, SessionId);
        PlmxFrame.EncodeHeader(
            _frameScratch,
            PanelChannelId.Transport,
            type,
            payload.Length,
            NextSequence(PanelChannelId.Transport),
            SessionId.Tag);
        payload.CopyTo(_frameScratch.AsSpan(PlmxFrame.HeaderSize));
        var total = PlmxFrame.HeaderSize + payload.Length;
        await _physicalStream.WriteAsync(_frameScratch.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
        await _physicalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _profile.FrameSent(coalesced: true);
    }

    internal async ValueTask WriteAsync(PanelChannelId channel, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_state != PlmxSessionState.Established)
            throw new InvalidOperationException("StartAsync must complete before channel I/O.");
        if (bytes.Length == 0) return;
        try
        {
            // Fragment size is a scheduling decision, not a protocol limit: a
            // smaller fragment lets the writer loop re-check the control queue
            // more often, which is what keeps touch latency bounded while video
            // saturates the pipe.
            var maximum = Math.Min(_fragmentBytes, PlmxFrame.MaximumPayloadFor(channel));
            for (var offset = 0; offset < bytes.Length; offset += maximum)
            {
                var count = Math.Min(maximum, bytes.Length - offset);
                // Slice, do not copy. The caller cannot observe or reuse its
                // buffer until this awaits through to the fragment's completion.
                await EnqueueAsync(channel, bytes.Slice(offset, count), PlmxFrameType.Data, cancellationToken)
                    .ConfigureAwait(false);
                _profile.FragmentSent();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PanelTransportDisconnectedException) { throw; }
        catch (Exception exception)
        {
            throw new PanelTransportDisconnectedException($"Logical channel {channel} disconnected.", exception);
        }
    }

    private async ValueTask EnqueueAsync(
        PanelChannelId channel,
        ReadOnlyMemory<byte> payload,
        PlmxFrameType type,
        CancellationToken cancellationToken)
    {
        PlmxFrame.ValidateLength(channel, payload.Length);
        IDisposable? reservation;
        try { reservation = _budget.Reserve(true, channel, payload.Length); }
        catch (InvalidDataException error) { Fail(error); throw; }
        var write = new OutboundWrite(channel, type, payload, Stopwatch.GetTimestamp(), reservation);
        var queue = channel == PanelChannelId.Video ? _videoWrites : _controlWrites;
        try
        {
            var queued = queue.Writer.WriteAsync(write, cancellationToken);
            if (_limits.IsWifi) await queued.AsTask().WaitAsync(RemainingWriteTime(write), cancellationToken).ConfigureAwait(false);
            else await queued.ConfigureAwait(false);
            SetDepth(channel, 1);
            _writeReady.Release();
            if (_limits.IsWifi) await write.Completion.Task.WaitAsync(RemainingWriteTime(write), cancellationToken).ConfigureAwait(false);
            else await write.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_limits.IsWifi) Fail(error);
            reservation?.Dispose();
            throw;
        }
    }

    private static TimeSpan RemainingWriteTime(OutboundWrite write)
    {
        var remaining = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(write.EnqueuedAt);
        return remaining > TimeSpan.Zero ? remaining : throw new TimeoutException("WiFi write deadline exceeded.");
    }

    private uint NextSequence(PanelChannelId channel) =>
        unchecked(++_txSequences[(byte)channel]);

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _writeReady.WaitAsync(cancellationToken).ConfigureAwait(false);
                OutboundWrite? write = null;
                if (_controlWrites.Reader.TryRead(out var control)) write = control;
                else if (_videoWrites.Reader.TryRead(out var video)) write = video;
                if (write is null) continue;
                try
                {
                    SetDepth(write.Channel, -1);
                    var payload = write.Payload;
                    PlmxFrame.EncodeHeader(
                        _headerScratch,
                        write.Channel,
                        write.Type,
                        payload.Length,
                        NextSequence(write.Channel),
                        SessionId.Tag);
                    var coalesced = payload.Length <= _fragmentBytes;
                    if (coalesced)
                    {
                        // One physical transfer per logical frame. Writing the
                        // header separately would make it its own short-packet USB
                        // transfer on every frame.
                        _headerScratch.CopyTo(_frameScratch.AsSpan());
                        payload.Span.CopyTo(_frameScratch.AsSpan(PlmxFrame.HeaderSize));
                        await _physicalStream
                            .WriteAsync(_frameScratch.AsMemory(0, PlmxFrame.HeaderSize + payload.Length), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _physicalStream.WriteAsync(_headerScratch, cancellationToken).ConfigureAwait(false);
                        await _physicalStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    }
                    await _physicalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    _profile.FrameSent(coalesced);
                    var latency = Stopwatch.GetElapsedTime(write.EnqueuedAt);
                    _metrics.Sent(
                        write.Channel,
                        payload.Length,
                        latency,
                        PlmxFrame.HeaderSize + payload.Length);
                    write.Completion.TrySetResult();
                }
                catch (Exception error) { write.Completion.TrySetException(error); throw; }
                finally { write.Reservation?.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(new PanelTransportDisconnectedException("Physical transport write failed.", exception));
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                PlmxFrameHeader header;
                try
                {
                    header = await _reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception) when (_role == PlmxRole.Accessory && !_limits.IsWifi)
                {
                    // A truncated or spliced frame means the stream is no longer
                    // aligned. The accessory recovers by searching forward for
                    // the next genuine handshake instead of tearing down the
                    // physical connection, which is what previously left the
                    // accessory descriptor unusable.
                    await ResyncAfterFramingErrorAsync(exception, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _limits.Validate(header.Channel, header.PayloadLength);
                var reservation = _budget.Reserve(false, header.Channel, header.PayloadLength);
                var payload = PooledPayload.Rent(header.PayloadLength, _profile, reservation, _limits.IsWifi);
                var accepted = false;
                try
                {
                    await _reader.ReadExactlyAsync(payload.Memory, cancellationToken).ConfigureAwait(false);
                    _profile.FrameReceived();
                    // A HELLO is checked before the session filter, because by
                    // definition it carries a tag this session does not have yet.
                    if (!_limits.IsWifi && _role == PlmxRole.Accessory &&
                        header is { Channel: PanelChannelId.Transport, Type: PlmxFrameType.Hello })
                    {
                        await ReadoptSessionAsync(header, payload, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (!IsCurrentSession(header)) continue;
                    _metrics.Received(
                        header.Channel,
                        payload.Length,
                        PlmxFrame.HeaderSize + payload.Length);
                    if (!ValidateSequence(header)) continue;
                    accepted = await DispatchAsync(header, payload, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // Ownership transfers to the logical stream only when the
                    // frame was queued for a consumer; every other path returns
                    // the buffer here so the pool cannot leak.
                    if (!accepted) payload.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception is PanelTransportDisconnectedException
                ? exception
                : new PanelTransportDisconnectedException("Physical transport disconnected mid-frame.", exception));
        }
    }

    /// <summary>
    /// The newest HELLO always wins.
    ///
    /// The accessory cannot tell, while searching a dirty stream, whether the
    /// first well-formed HELLO it finds is the current one or the predecessor's
    /// greeting still sitting in the pipe. Because a bulk pipe is FIFO, a stale
    /// HELLO always arrives before the real one, so superseding on every later
    /// HELLO converges on the correct session without anyone having to know how
    /// many stale bytes existed. The host, which owns the nonce, simply ignores
    /// acknowledgements that do not match the one it generated.
    /// </summary>
    private async ValueTask ReadoptSessionAsync(
        PlmxFrameHeader header,
        PooledPayload payload,
        CancellationToken cancellationToken)
    {
        if (!PlmxFrame.IsHandshakeFrame(header, payload.Span, PlmxFrameType.Hello, out var hello))
            throw new InvalidDataException("Malformed HELLO frame after the session was established.");
        await AdoptSessionAsync(hello, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers alignment after a framing error by scanning forward to the next
    /// genuine HELLO. Bounded by the resync limits, so a stream that never
    /// contains one fails the session rather than spinning.
    /// </summary>
    private async ValueTask ResyncAfterFramingErrorAsync(Exception cause, CancellationToken cancellationToken)
    {
        _state = PlmxSessionState.Recovering;
        _log.WriteLine(
            $"[PLMX] session={SessionId.Prefix} lost frame alignment ({cause.Message}); " +
            "state=SearchingForHandshake");
        var hello = await SearchAsync(PlmxFrameType.Hello, _ => true, cancellationToken).ConfigureAwait(false);
        await AdoptSessionAsync(hello, cancellationToken).ConfigureAwait(false);
        _state = PlmxSessionState.Established;
    }

    private async ValueTask AdoptSessionAsync(PlmxHello hello, CancellationToken cancellationToken)
    {
        if (hello.SessionId == SessionId)
        {
            // A duplicate greeting for the session already in force is
            // idempotent: re-acknowledge rather than tear anything down.
            await EnqueueAsync(
                PanelChannelId.Transport,
                PlmxFrame.EncodeHello(Capabilities, SessionId),
                PlmxFrameType.HelloAck,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        ValidateCapabilities(hello);
        _log.WriteLine(
            $"[PLMX] session={SessionId.Prefix} superseded by session={hello.SessionId.Prefix}; " +
            "re-adopting and resetting channels");

        // Everything scoped to the previous session dies with it. Nothing is
        // carried forward: queues, channels and receive sequences all reset.
        lock (_channelLock)
        {
            foreach (var stream in _channels.Values)
                stream.Complete(new PanelTransportDisconnectedException("The transport session was superseded."));
            _channels.Clear();
        }
        foreach (var accepted in _acceptedChannels.Values)
        {
            while (accepted.Reader.TryRead(out var abandoned))
                abandoned.Complete(new PanelTransportDisconnectedException("The transport session was superseded."));
        }
        // Only receive state is reset. Transmit sequences keep climbing because
        // the writer thread owns them, and the new peer starts with no
        // expectation for this channel anyway.
        Array.Clear(_rxSeen);
        Array.Clear(_rxSequences);
        SessionId = hello.SessionId;
        _metrics.Resynchronized(0);
        await EnqueueAsync(
            PanelChannelId.Transport,
            PlmxFrame.EncodeHello(Capabilities, SessionId),
            PlmxFrameType.HelloAck,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A frame tagged for a different session belongs to a predecessor whose
    /// bytes were still in the pipe. Dropping it is the whole point of v2: under
    /// v1 it would have been decoded as this session's and killed the channel.
    /// </summary>
    private bool IsCurrentSession(in PlmxFrameHeader header)
    {
        if (header.SessionTag == SessionId.Tag) return true;
        _metrics.StaleFrameDropped();
        return false;
    }

    /// <summary>
    /// Control and Assets are strictly ordered: the carrier is a reliable
    /// in-order pipe, so a gap there means corruption and must fail the session.
    /// Video is monotonic only, because latest-frame-wins upstream legitimately
    /// leaves holes; old or duplicate video is dropped rather than fatal.
    /// </summary>
    private bool ValidateSequence(in PlmxFrameHeader header)
    {
        var index = (byte)header.Channel;
        var sequence = header.Sequence;
        if (!_rxSeen[index])
        {
            _rxSeen[index] = true;
            _rxSequences[index] = sequence;
            return true;
        }
        var previous = _rxSequences[index];
        var delta = unchecked((int)(sequence - previous));
        if (header.Channel == PanelChannelId.Video)
        {
            if (delta <= 0)
            {
                _metrics.StaleFrameDropped();
                return false;
            }
            if (delta > 1) _metrics.SequenceGap();
            _rxSequences[index] = sequence;
            return true;
        }
        if (delta != 1)
        {
            throw new InvalidDataException(
                $"{header.Channel} sequence {sequence} broke strict ordering after {previous}.");
        }
        _rxSequences[index] = sequence;
        return true;
    }

    /// <summary>
    /// Returns true when the payload buffer was handed to a logical stream and
    /// must not be released by the reader loop.
    /// </summary>
    private async ValueTask<bool> DispatchAsync(
        PlmxFrameHeader header,
        PooledPayload payload,
        CancellationToken cancellationToken)
    {
        if (header.Channel == PanelChannelId.Transport)
        {
            // The handshake completed before the reader loop started, so a
            // transport frame here is a peer restarting mid-stream.
            throw new InvalidDataException($"Unexpected {header.Type} frame after the session was established.");
        }
        switch (header.Type)
        {
            case PlmxFrameType.Open:
            {
                LogicalChannelStream? opened = null;
                lock (_channelLock)
                {
                    if (_limits.IsWifi)
                    {
                        if (_channels.ContainsKey(header.Channel)) throw new InvalidDataException("Duplicate WiFi OPEN.");
                    }
                    if (!_limits.IsWifi || _openRate.TryRecord(header.Channel))
                    {
                        _channels.Remove(header.Channel, out var previous);
                        previous?.Complete(new PanelTransportDisconnectedException("Logical channel was replaced."));
                        opened = new LogicalChannelStream(this, header.Channel);
                        _channels[header.Channel] = opened;
                    }
                }
                if (opened is null)
                {
                    await EnqueueAsync(header.Channel, ReadOnlyMemory<byte>.Empty, PlmxFrameType.Fin, cancellationToken).ConfigureAwait(false);
                    return false;
                }
                if (_limits.IsWifi)
                {
                    if (!_acceptedChannels[header.Channel].Writer.TryWrite(opened))
                        throw new InvalidDataException("WiFi accepted-channel queue exceeded.");
                }
                else await _acceptedChannels[header.Channel].Writer.WriteAsync(opened, cancellationToken).ConfigureAwait(false);
                return false;
            }
            case PlmxFrameType.Fin:
            {
                LogicalChannelStream? closing;
                lock (_channelLock) _channels.Remove(header.Channel, out closing);
                closing?.Complete();
                return false;
            }
            case PlmxFrameType.Data:
            {
                LogicalChannelStream stream;
                lock (_channelLock)
                {
                    if (!_channels.TryGetValue(header.Channel, out stream!))
                        throw new InvalidDataException($"Data arrived for unopened channel {header.Channel}.");
                }
                await stream.ReceiveAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            default:
                throw new InvalidDataException($"Unexpected PLMX frame type {header.Type}.");
        }
    }

    private void SetDepth(PanelChannelId channel, int change)
    {
        if (channel == PanelChannelId.Video)
            _metrics.SetQueueDepth(channel, Math.Max(0, Interlocked.Add(ref _videoDepth, change)));
        else
            _metrics.SetQueueDepth(PanelChannelId.Control, Math.Max(0, Interlocked.Add(ref _controlDepth, change)));
    }

    private void Fail(Exception exception, bool countError = true)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            if (countError)
            {
                _metrics.Error();
                _log.WriteLine(
                    $"[PLMX] session={SessionId.Prefix} failed in state={_state}: " +
                    $"{exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}");
            }
            _state = PlmxSessionState.Closing;
        }
        _controlWrites.Writer.TryComplete(exception);
        _videoWrites.Writer.TryComplete(exception);
        while (_controlWrites.Reader.TryRead(out var control)) { control.Reservation?.Dispose(); control.Completion.TrySetException(exception); }
        while (_videoWrites.Reader.TryRead(out var video)) { video.Reservation?.Dispose(); video.Completion.TrySetException(exception); }
        lock (_channelLock)
        {
            foreach (var stream in _limits.IsWifi ? _wifiStreams.ToArray() : _channels.Values.ToArray()) stream.Complete(exception);
            _channels.Clear();
        }
        foreach (var accepted in _acceptedChannels.Values)
        {
            accepted.Writer.TryComplete(exception);
            if (_limits.IsWifi) while (accepted.Reader.TryRead(out var abandoned)) abandoned.Complete(exception);
        }
        _lifetime.Cancel();
        if (_limits.IsWifi) { try { _physicalStream.Dispose(); } catch (Exception) { } }
    }

    private async ValueTask CloseChannelAsync(PanelChannelId channel, LogicalChannelStream owner)
    {
        var removed = false;
        lock (_channelLock)
            if (_channels.TryGetValue(channel, out var current) && ReferenceEquals(current, owner)) removed = _channels.Remove(channel);
        if (!removed || _lifetime.IsCancellationRequested) return;
        try { await EnqueueAsync(channel, ReadOnlyMemory<byte>.Empty, PlmxFrameType.Fin, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _state = PlmxSessionState.Closing;
        _lifetime.Cancel();
        Fail(new PanelTransportDisconnectedException("Transport session closed."), countError: false);
        // Closing the physical handle aborts any in-flight overlapped transfer,
        // so shutdown cannot wait on a read that will never be answered.
        await _physicalStream.DisposeAsync().ConfigureAwait(false);
        var tasks = new[] { _readerTask, _writerTask }.Where(task => task is not null).Cast<Task>();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
        _writeReady.Dispose();
        _lifetime.Dispose();
        _state = PlmxSessionState.Disconnected;
    }

    private sealed record OutboundWrite(
        PanelChannelId Channel,
        PlmxFrameType Type,
        ReadOnlyMemory<byte> Payload,
        long EnqueuedAt,
        IDisposable? Reservation)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A received frame payload with a single explicit owner. The reader loop
    /// owns it until a logical stream accepts it; the consumer owns it until it
    /// is fully drained or the channel completes. It is never returned to the
    /// pool while any reader can still observe it.
    /// </summary>
    private sealed class PooledPayload
    {
        private static readonly byte[] Empty = [];
        private readonly TransportIoProfile? _profile;
        private byte[]? _array;
        private IDisposable? _reservation;
        private bool _pooled;

        private PooledPayload(byte[]? array, int length, TransportIoProfile? profile)
        {
            _array = array;
            Length = length;
            _profile = profile;
        }

        public int Length { get; }
        public Memory<byte> Memory => _array is null ? Memory<byte>.Empty : _array.AsMemory(0, Length);
        public ReadOnlySpan<byte> Span => _array is null ? ReadOnlySpan<byte>.Empty : _array.AsSpan(0, Length);

        public static PooledPayload Rent(int length, TransportIoProfile? profile, IDisposable? reservation = null, bool exact = false)
        {
            if (length == 0) return new PooledPayload(Empty, 0, null) { _reservation = reservation };
            profile?.PayloadRented();
            try { return new PooledPayload(exact ? new byte[length] : ArrayPool<byte>.Shared.Rent(length), length, profile)
                { _reservation = reservation, _pooled = !exact }; }
            catch { reservation?.Dispose(); throw; }
        }

        public void Release()
        {
            var array = Interlocked.Exchange(ref _array, null);
            Interlocked.Exchange(ref _reservation, null)?.Dispose();
            if (array is null || array.Length == 0) return;
            if (_pooled) ArrayPool<byte>.Shared.Return(array);
            _profile?.PayloadReturned();
        }
    }

    private sealed class LogicalChannelStream : Stream
    {
        private readonly MultiplexedTransportSession _owner;
        private readonly PanelChannelId _channel;
        private readonly Channel<PooledPayload> _incoming = Channel.CreateUnbounded<PooledPayload>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private PooledPayload? _current;
        private int _currentOffset;
        private int _disposed;
        private bool _ended;

        public LogicalChannelStream(MultiplexedTransportSession owner, PanelChannelId channel)
        {
            _owner = owner;
            _channel = channel;
            if (owner._limits.IsWifi) owner._wifiStreams.Add(this);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            try
            {
                while (_current is null || _currentOffset == _current.Length)
                {
                    ReleaseCurrent();
                    if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                    if (!_incoming.Reader.TryRead(out _current)) continue;
                    _currentOffset = 0;
                }
                var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
                _current.Span.Slice(_currentOffset, count).CopyTo(buffer.Span);
                _currentOffset += count;
                if (_currentOffset == _current.Length) ReleaseCurrent();
                return count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (PanelTransportDisconnectedException) { throw; }
            catch (Exception exception)
            {
                throw new PanelTransportDisconnectedException($"Logical channel {_channel} disconnected.", exception);
            }
        }

        private void ReleaseCurrent()
        {
            var current = Interlocked.Exchange(ref _current, null);
            current?.Release();
            _currentOffset = 0;
            ForgetIfDrained();
        }

        private void ForgetIfDrained()
        {
            if (_owner._limits.IsWifi && _ended && _current is null && !_incoming.Reader.TryPeek(out _))
                lock (_owner._channelLock) _owner._wifiStreams.Remove(this);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _owner.WriteAsync(_channel, buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        internal ValueTask ReceiveAsync(PooledPayload payload, CancellationToken cancellationToken) =>
            _incoming.Writer.WriteAsync(payload, cancellationToken);

        internal void Complete(Exception exception)
        {
            _ended = true;
            _incoming.Writer.TryComplete(exception);
            DrainQueued();
            if (_owner._limits.IsWifi) ReleaseCurrent();
            ForgetIfDrained();
        }

        internal void Complete()
        {
            _ended = true;
            _incoming.Writer.TryComplete();
            ForgetIfDrained();
            // Queued payloads stay readable until the consumer drains them; a
            // graceful FIN does not discard data, so nothing is released here.
        }

        private void DrainQueued()
        {
            while (_incoming.Reader.TryRead(out var abandoned)) abandoned.Release();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.CloseChannelAsync(_channel, this).AsTask().GetAwaiter().GetResult();
                ReleaseCurrent();
                DrainQueued();
                _ended = true; ForgetIfDrained();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _owner.CloseChannelAsync(_channel, this).ConfigureAwait(false);
                ReleaseCurrent();
                DrainQueued();
                _ended = true; ForgetIfDrained();
            }
            GC.SuppressFinalize(this);
        }
    }
}
