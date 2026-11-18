using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PanelHost.Transport;

internal enum PanelChannelId : byte
{
    Transport = 0,
    Control = 1,
    Video = 2,
    Assets = 3,
    Audio = 4,
}

[Flags]
internal enum PanelTransportCapabilities : uint
{
    None = 0,
    Multiplexing = 1 << 0,
    ControlPriority = 1 << 1,
    VideoBackpressure = 1 << 2,

    /// <summary>PLMX v2 binds every frame to a 128-bit session nonce.</summary>
    SessionIdentity = 1 << 3,

    /// <summary>The peer can resynchronize onto a handshake in a dirty stream.</summary>
    Resync = 1 << 4,
}

internal interface IPanelConnection : IAsyncDisposable
{
    PanelChannelId Channel { get; }
    Stream Stream { get; }
    string Description { get; }
    ValueTask CloseAsync();
}

internal interface IPanelConnectionListener : IAsyncDisposable
{
    PanelChannelId Channel { get; }
    int? BoundPort { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask<IPanelConnection> AcceptAsync(CancellationToken cancellationToken);
}

internal interface IPanelTransport : IAsyncDisposable
{
    string Name { get; }
    PanelTransportMetrics Metrics { get; }
    IPanelConnectionListener Listen(PanelChannelId channel);
}

internal sealed class PanelTransportDisconnectedException : IOException
{
    public PanelTransportDisconnectedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class PanelTransportMetrics
{
    private long _controlTx;
    private long _controlRx;
    private long _videoTx;
    private long _videoRx;
    private long _controlLatencyTicks;
    private long _controlWrites;
    private long _videoDrops;
    private long _reconnects;
    private long _payloadBytesTx;
    private long _payloadBytesRx;
    private long _wireBytesTx;
    private long _wireBytesRx;
    private long _errors;
    private long _sessionsStarted;
    private long _reconnectAttempts;
    private long _reconnectSuccesses;
    private long _resyncEvents;
    private long _resyncDiscardedBytes;
    private long _staleFramesDropped;
    private long _handshakeFailures;
    private long _sequenceGaps;
    private long _authFailures;
    private int _controlQueueDepth;
    private int _videoQueueDepth;

    /// <summary>Physical-layer counters used to attribute transport CPU.</summary>
    public TransportIoProfile Io { get; } = new();

    public long ControlBytesTx => Interlocked.Read(ref _controlTx);
    public long ControlBytesRx => Interlocked.Read(ref _controlRx);
    public long VideoBytesTx => Interlocked.Read(ref _videoTx);
    public long VideoBytesRx => Interlocked.Read(ref _videoRx);
    public long VideoDrops => Interlocked.Read(ref _videoDrops);
    public long Reconnects => Interlocked.Read(ref _reconnects);
    public long PayloadBytesTx => Interlocked.Read(ref _payloadBytesTx);
    public long PayloadBytesRx => Interlocked.Read(ref _payloadBytesRx);
    public long WireBytesTx => Interlocked.Read(ref _wireBytesTx);
    public long WireBytesRx => Interlocked.Read(ref _wireBytesRx);
    public long Errors => Interlocked.Read(ref _errors);
    public long SessionsStarted => Interlocked.Read(ref _sessionsStarted);
    public long ReconnectAttempts => Interlocked.Read(ref _reconnectAttempts);
    public long ReconnectSuccesses => Interlocked.Read(ref _reconnectSuccesses);
    public long ResyncEvents => Interlocked.Read(ref _resyncEvents);
    public long ResyncDiscardedBytes => Interlocked.Read(ref _resyncDiscardedBytes);
    public long StaleFramesDropped => Interlocked.Read(ref _staleFramesDropped);
    public long HandshakeFailures => Interlocked.Read(ref _handshakeFailures);
    public long SequenceGaps => Interlocked.Read(ref _sequenceGaps);
    public long AuthFailures => Interlocked.Read(ref _authFailures);
    public int ControlQueueDepth => Volatile.Read(ref _controlQueueDepth);
    public int VideoQueueDepth => Volatile.Read(ref _videoQueueDepth);
    public TimeSpan AverageControlLatency => Interlocked.Read(ref _controlWrites) is var writes && writes > 0
        ? TimeSpan.FromTicks(Interlocked.Read(ref _controlLatencyTicks) / writes)
        : TimeSpan.Zero;

    internal void Sent(PanelChannelId channel, int bytes, TimeSpan queuedFor, int? wireBytes = null)
    {
        Interlocked.Add(ref _payloadBytesTx, bytes);
        Interlocked.Add(ref _wireBytesTx, wireBytes ?? bytes);
        if (channel == PanelChannelId.Control)
        {
            Interlocked.Add(ref _controlTx, bytes);
            Interlocked.Add(ref _controlLatencyTicks, queuedFor.Ticks);
            Interlocked.Increment(ref _controlWrites);
        }
        else if (channel == PanelChannelId.Video)
        {
            Interlocked.Add(ref _videoTx, bytes);
        }
    }

    internal void Received(PanelChannelId channel, int bytes, int? wireBytes = null)
    {
        Interlocked.Add(ref _payloadBytesRx, bytes);
        Interlocked.Add(ref _wireBytesRx, wireBytes ?? bytes);
        if (channel == PanelChannelId.Control)
        {
            Interlocked.Add(ref _controlRx, bytes);
        }
        else if (channel == PanelChannelId.Video)
        {
            Interlocked.Add(ref _videoRx, bytes);
        }
    }

    internal void SetQueueDepth(PanelChannelId channel, int depth)
    {
        if (channel == PanelChannelId.Control)
        {
            Volatile.Write(ref _controlQueueDepth, depth);
        }
        else if (channel == PanelChannelId.Video)
        {
            Volatile.Write(ref _videoQueueDepth, depth);
        }
    }

    internal void VideoDropped() => Interlocked.Increment(ref _videoDrops);
    internal void Reconnected() => Interlocked.Increment(ref _reconnects);
    internal void Error() => Interlocked.Increment(ref _errors);
    internal void SessionStarted() => Interlocked.Increment(ref _sessionsStarted);
    internal void ReconnectAttempted() => Interlocked.Increment(ref _reconnectAttempts);
    internal void ReconnectSucceeded() => Interlocked.Increment(ref _reconnectSuccesses);
    internal void HandshakeFailed() => Interlocked.Increment(ref _handshakeFailures);
    internal void StaleFrameDropped() => Interlocked.Increment(ref _staleFramesDropped);
    internal void SequenceGap() => Interlocked.Increment(ref _sequenceGaps);
    internal void AuthenticationFailed() => Interlocked.Increment(ref _authFailures);

    internal void Resynchronized(long discardedBytes)
    {
        Interlocked.Increment(ref _resyncEvents);
        Interlocked.Add(ref _resyncDiscardedBytes, discardedBytes);
    }

    internal PanelTransportMetricsSnapshot Snapshot() => new(
        ControlBytesTx,
        ControlBytesRx,
        VideoBytesTx,
        VideoBytesRx,
        PayloadBytesTx,
        PayloadBytesRx,
        WireBytesTx,
        WireBytesRx,
        VideoDrops,
        Reconnects,
        Errors,
        ControlQueueDepth,
        VideoQueueDepth);

    internal async Task LogAsync(TextWriter log, string transportName, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            log.WriteLine(
                $"[TransportMetrics] name={transportName} " +
                $"controlTx={ControlBytesTx} controlRx={ControlBytesRx} " +
                $"videoTx={VideoBytesTx} videoRx={VideoBytesRx} " +
                $"controlQueue={ControlQueueDepth} videoQueue={VideoQueueDepth} " +
                $"controlLatencyMs={AverageControlLatency.TotalMilliseconds:F3} " +
                $"videoDrops={VideoDrops} reconnects={Reconnects} errors={Errors} " +
                $"sessions={SessionsStarted} reconnectAttempts={ReconnectAttempts} " +
                $"reconnectSuccesses={ReconnectSuccesses} resyncEvents={ResyncEvents} " +
                $"resyncDiscardedBytes={ResyncDiscardedBytes} staleFrames={StaleFramesDropped} " +
                $"handshakeFailures={HandshakeFailures} sequenceGaps={SequenceGaps} " +
                $"authFailures={AuthFailures} " +
                $"payloadTx={PayloadBytesTx} wireTx={WireBytesTx} " +
                $"payloadRx={PayloadBytesRx} wireRx={WireBytesRx}");
        }
    }
}

internal sealed class AdbTcpTransport : IPanelTransport
{
    private readonly int _controlPort;
    private readonly int _videoPort;
    private readonly byte[] _authenticationToken;

    public AdbTcpTransport(
        ReadOnlySpan<byte> authenticationToken,
        int controlPort = Protocol.PanelProtocol.Port,
        int videoPort = Video.PanelVideoProtocol.Port)
    {
        if (authenticationToken.Length != AdbTcpAuthentication.TokenBytes)
            throw new ArgumentException("ADB/TCP authentication token must be 256 bits.", nameof(authenticationToken));
        _authenticationToken = authenticationToken.ToArray();
        _controlPort = controlPort;
        _videoPort = videoPort;
    }

    public string Name => "adb-tcp";
    public PanelTransportMetrics Metrics { get; } = new();

    public IPanelConnectionListener Listen(PanelChannelId channel) => channel switch
    {
        PanelChannelId.Control => new TcpPanelConnectionListener(channel, _controlPort, Metrics, _authenticationToken),
        PanelChannelId.Video => new TcpPanelConnectionListener(channel, _videoPort, Metrics, _authenticationToken),
        PanelChannelId.Assets => new TcpPanelConnectionListener(channel, TransportBenchmarkProtocol.Port, Metrics, _authenticationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "ADB/TCP exposes control, video, and benchmark assets."),
    };

    public ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_authenticationToken);
        return ValueTask.CompletedTask;
    }
}

internal readonly record struct PanelTransportMetricsSnapshot(
    long ControlBytesTx,
    long ControlBytesRx,
    long VideoBytesTx,
    long VideoBytesRx,
    long PayloadBytesTx,
    long PayloadBytesRx,
    long WireBytesTx,
    long WireBytesRx,
    long VideoDrops,
    long Reconnects,
    long Errors,
    int ControlQueueDepth,
    int VideoQueueDepth)
{
    public static PanelTransportMetricsSnapshot operator -(
        PanelTransportMetricsSnapshot end,
        PanelTransportMetricsSnapshot start) => new(
            end.ControlBytesTx - start.ControlBytesTx,
            end.ControlBytesRx - start.ControlBytesRx,
            end.VideoBytesTx - start.VideoBytesTx,
            end.VideoBytesRx - start.VideoBytesRx,
            end.PayloadBytesTx - start.PayloadBytesTx,
            end.PayloadBytesRx - start.PayloadBytesRx,
            end.WireBytesTx - start.WireBytesTx,
            end.WireBytesRx - start.WireBytesRx,
            end.VideoDrops - start.VideoDrops,
            end.Reconnects - start.Reconnects,
            end.Errors - start.Errors,
            end.ControlQueueDepth,
            end.VideoQueueDepth);
}

internal sealed class TcpPanelConnectionListener : IPanelConnectionListener
{
    private readonly TcpListener _listener;
    private readonly PanelTransportMetrics _metrics;
    private readonly byte[]? _authenticationToken;
    private bool _started;

    public TcpPanelConnectionListener(
        PanelChannelId channel,
        int port,
        PanelTransportMetrics? metrics = null,
        ReadOnlySpan<byte> authenticationToken = default)
    {
        if (port is < 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        Channel = channel;
        _metrics = metrics ?? new PanelTransportMetrics();
        _authenticationToken = authenticationToken.IsEmpty ? null : authenticationToken.ToArray();
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public PanelChannelId Channel { get; }
    public int? BoundPort => _started ? ((IPEndPoint)_listener.LocalEndpoint).Port : null;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _listener.Start();
        _started = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IPanelConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                if (_authenticationToken is null) return new TcpPanelConnection(Channel, client, _metrics);
                try
                {
                    if (await AdbTcpAuthentication.AuthenticateServerAsync(
                            client.GetStream(), Channel, _authenticationToken, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return new TcpPanelConnection(Channel, client, _metrics);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    client.Dispose();
                    throw;
                }
                catch (Exception exception) when (exception is IOException or SocketException)
                {
                    // Authentication failures are deliberately indistinguishable to the peer.
                }
                client.Dispose();
                _metrics.AuthenticationFailed();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException exception)
        {
            throw new PanelTransportDisconnectedException("ADB/TCP listener failed.", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_authenticationToken is not null)
            CryptographicOperations.ZeroMemory(_authenticationToken);
        return ValueTask.CompletedTask;
    }
}

internal sealed class TcpPanelConnection : IPanelConnection
{
    private readonly TcpClient _client;

    public TcpPanelConnection(PanelChannelId channel, TcpClient client, PanelTransportMetrics metrics)
    {
        Channel = channel;
        _client = client;
        Stream = new InstrumentedTransportStream(client.GetStream(), channel, metrics);
        Description = client.Client.RemoteEndPoint?.ToString() ?? "adb-tcp";
    }

    public PanelChannelId Channel { get; }
    public Stream Stream { get; }
    public string Description { get; }

    public ValueTask CloseAsync()
    {
        _client.Close();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}

internal sealed class InstrumentedTransportStream : Stream
{
    private readonly Stream _inner;
    private readonly PanelChannelId _channel;
    private readonly PanelTransportMetrics _metrics;

    public InstrumentedTransportStream(Stream inner, PanelChannelId channel, PanelTransportMetrics metrics)
    {
        _inner = inner;
        _channel = channel;
        _metrics = metrics;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var read = _inner.Read(buffer, offset, count);
            _metrics.Received(_channel, read);
            return read;
        }
        catch (IOException exception) { throw Normalize(exception); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _metrics.Received(_channel, read);
            return read;
        }
        catch (IOException exception) { throw Normalize(exception); }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _inner.Write(buffer, offset, count);
            _metrics.Sent(_channel, count, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
        catch (IOException exception) { throw Normalize(exception); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _metrics.Sent(_channel, buffer.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
        catch (IOException exception) { throw Normalize(exception); }
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
    private static PanelTransportDisconnectedException Normalize(IOException exception) =>
        exception as PanelTransportDisconnectedException ??
        new PanelTransportDisconnectedException("Transport disconnected.", exception);
}

internal sealed class StreamPanelConnection : IPanelConnection
{
    private readonly bool _ownsStream;

    public StreamPanelConnection(PanelChannelId channel, Stream stream, string description, bool ownsStream = true)
    {
        Channel = channel;
        Stream = stream;
        Description = description;
        _ownsStream = ownsStream;
    }

    public PanelChannelId Channel { get; }
    public Stream Stream { get; }
    public string Description { get; }

    public async ValueTask CloseAsync()
    {
        if (_ownsStream) await Stream.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsStream) await Stream.DisposeAsync().ConfigureAwait(false);
    }
}
