using System.Threading.Channels;

namespace PanelHost.Transport;

/// <summary>
/// Experimental native carrier. With no explicit path it discovers WinUSB,
/// performs the Android Open Accessory transition, selects FF/FF/00 bulk
/// endpoints, and owns reconnect of the entire multiplexed session.
///
/// PanelHost is always the PLMX host: it generates the session nonce and drives
/// the handshake. Each physical connection gets a brand new nonce, so a session
/// can never be confused with its predecessor even when the previous session's
/// bytes are still sitting in the pipe.
/// </summary>
internal sealed class NativeUsbTransport : IPanelTransport
{
    private static readonly TimeSpan MinimumReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(4);
    private readonly string? _devicePath;
    private readonly TextWriter _log;
    private readonly NativeUsbTransportOptions _options;
    private readonly Func<CancellationToken, ValueTask<Stream>> _openStream;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private MultiplexedTransportSession? _session;
    private int _sessionGeneration;
    private bool _disposed;

    public NativeUsbTransport(
        string? devicePath = null,
        TextWriter? log = null,
        Func<CancellationToken, ValueTask<Stream>>? openStream = null,
        NativeUsbTransportOptions? options = null)
    {
        _devicePath = devicePath;
        _log = log ?? Console.Out;
        _options = options ?? NativeUsbTransportOptions.FromEnvironment();
        _openStream = openStream ??
            (token => WindowsUsbAccessory.OpenAsync(_devicePath, _log, token, _options, Metrics.Io));
    }

    public string Name => "usb-native-experimental";
    public PanelTransportMetrics Metrics { get; } = new();

    /// <summary>
    /// Increments on every physical session. Anything holding an older
    /// generation belongs to a dead session and must not touch the new one.
    /// </summary>
    public int SessionGeneration => Volatile.Read(ref _sessionGeneration);

    public IPanelConnectionListener Listen(PanelChannelId channel)
    {
        if (channel is not (PanelChannelId.Control or PanelChannelId.Video or PanelChannelId.Assets))
            throw new ArgumentOutOfRangeException(nameof(channel));
        return new NativeUsbListener(this, channel);
    }

    private async ValueTask<IPanelConnection> AcceptAsync(PanelChannelId channel, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            var (session, generation) = await GetOrCreateSessionAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var stream = await session.AcceptChannelAsync(channel, linked.Token).ConfigureAwait(false);
                return new StreamPanelConnection(channel, stream, Name, ownsStream: true);
            }
            catch (Exception exception) when (exception is IOException or ChannelClosedException)
            {
                await InvalidateAsync(session, generation).ConfigureAwait(false);
                Metrics.Reconnected();
                _log.WriteLine($"[Transport] USB disconnected; reconnecting session: {exception.Message}");
            }
        }
    }

    private async ValueTask<(MultiplexedTransportSession Session, int Generation)> GetOrCreateSessionAsync(
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) return (_session, SessionGeneration);
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MultiplexedTransportSession? openingSession = null;
                Metrics.ReconnectAttempted();
                try
                {
                    var stream = await _openStream(cancellationToken).ConfigureAwait(false);
                    openingSession = new MultiplexedTransportSession(
                        stream,
                        PlmxRole.Host,
                        Metrics,
                        profile: Metrics.Io,
                        log: _log);
                    await openingSession.StartAsync(cancellationToken).ConfigureAwait(false);
                    _session = openingSession;
                    Interlocked.Increment(ref _sessionGeneration);
                    Metrics.ReconnectSucceeded();
                    _log.WriteLine(
                        $"[Transport] Native USB session established (PLMX v{PlmxFrame.Version}); " +
                        $"session={openingSession.SessionId.Prefix} generation={SessionGeneration} " +
                        $"{_options} fragment={openingSession.FragmentBytes}B.");
                    return (openingSession, SessionGeneration);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Tearing the physical stream down between attempts is what
                    // makes a retry meaningful: a half-open handle would hand
                    // the next attempt the same dirty pipe.
                    if (openingSession is not null)
                        await openingSession.DisposeAsync().ConfigureAwait(false);
                    var root = exception.GetBaseException();
                    var discarded = exception is PlmxResyncException resync ? resync.DiscardedBytes : 0;
                    _log.WriteLine(
                        $"[Transport] Native USB unavailable (attempt {++attempt}): " +
                        $"{root.GetType().Name}: {root.Message}" +
                        (discarded > 0 ? $" after discarding {discarded} stale bytes" : string.Empty));
                    await Task.Delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Exponential backoff bounded so a device that is briefly wedged is retried
    /// promptly while a permanently absent one does not spin.
    /// </summary>
    private static TimeSpan BackoffFor(int attempt)
    {
        var scaled = MinimumReconnectDelay * Math.Pow(2, Math.Min(attempt - 1, 5));
        return scaled > MaximumReconnectDelay ? MaximumReconnectDelay : scaled;
    }

    private async ValueTask InvalidateAsync(MultiplexedTransportSession failed, int generation)
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A late failure from an already-replaced session must not tear down
            // its successor.
            if (!ReferenceEquals(_session, failed) || generation != SessionGeneration) return;
            _session = null;
            await failed.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class NativeUsbListener : IPanelConnectionListener
    {
        private readonly NativeUsbTransport _owner;

        public NativeUsbListener(NativeUsbTransport owner, PanelChannelId channel)
        {
            _owner = owner;
            Channel = channel;
        }

        public PanelChannelId Channel { get; }
        public int? BoundPort => null;
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public ValueTask<IPanelConnection> AcceptAsync(CancellationToken cancellationToken) =>
            _owner.AcceptAsync(Channel, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
