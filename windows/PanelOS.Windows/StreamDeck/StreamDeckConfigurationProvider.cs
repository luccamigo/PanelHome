using System.Threading.Channels;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Tells the provider that the backing configuration may have changed.
/// </summary>
/// <remarks>
/// The store writes atomically - a temporary file next to the target, then a
/// replace - so a single logical save can surface as Created, Changed or
/// Renamed depending on how the filesystem reports the swap. An implementation
/// must raise <see cref="Changed"/> for all of them and may raise it more often
/// than needed; the provider debounces and re-reads.
/// </remarks>
public interface IStreamDeckConfigurationChangeSource : IDisposable
{
    event EventHandler? Changed;

    void Start();
}

public sealed class StreamDeckSnapshotChangedEventArgs(StreamDeckSnapshot snapshot)
    : EventArgs
{
    public StreamDeckSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Timings of the reload loop, overridable for tests.</summary>
public sealed record StreamDeckReloadOptions
{
    /// <summary>
    /// How long to wait for the filesystem to stop reporting events before
    /// reading. One save produces several notifications; reading on the first
    /// one usually catches the file mid-swap.
    /// </summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Pause between attempts when the file is not readable yet.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Extra attempts after the first one. Bounded on purpose.</summary>
    public int MaximumRetries { get; init; } = 3;

    public static StreamDeckReloadOptions Default { get; } = new();
}

/// <summary>
/// Keeps a current <see cref="StreamDeckSnapshot"/> in sync with the file the
/// Control Center writes, without either side knowing about the other.
/// </summary>
/// <remarks>
/// <para>
/// A configuration that cannot be read - because the atomic replace is in
/// flight, or because the document is genuinely broken - never replaces a good
/// snapshot with an empty one. The read is retried a bounded number of times
/// and, if it keeps failing, the last known-good snapshot stays published and
/// the failure is logged.
/// </para>
/// <para>
/// The snapshot is only republished when its revision actually changes, so
/// rewriting an identical file does not push a layout to the tablet.
/// </para>
/// </remarks>
public sealed class StreamDeckConfigurationProvider : IAsyncDisposable
{
    private readonly IStreamDeckConfigurationStore _store;
    private readonly IStreamDeckConfigurationChangeSource? _changeSource;
    private readonly IStreamDeckLog _log;
    private readonly StreamDeckReloadOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _snapshotLock = new();

    private StreamDeckSnapshot _snapshot = StreamDeckSnapshot.Empty;
    private Task? _worker;
    private bool _disposed;

    public StreamDeckConfigurationProvider(
        IStreamDeckConfigurationStore store,
        IStreamDeckConfigurationChangeSource? changeSource = null,
        IStreamDeckLog? log = null,
        StreamDeckReloadOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _changeSource = changeSource;
        _log = log ?? NullStreamDeckLog.Instance;
        _options = options ?? StreamDeckReloadOptions.Default;
        _delay = delay ?? Task.Delay;
    }

    public event EventHandler<StreamDeckSnapshotChangedEventArgs>? SnapshotChanged;

    public StreamDeckSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Reads the configuration once and starts watching for changes.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);

        if (_changeSource is not null)
        {
            _changeSource.Changed += OnChangeSourceChanged;
            _changeSource.Start();
            _worker = Task.Run(
                () => ProcessSignalsAsync(_shutdown.Token),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Reads the configuration now, bypassing the debounce. Used for the first
    /// load and by tests; the watcher path goes through the signal loop.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var loaded = await LoadOnceAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                Publish(StreamDeckSnapshot.Create(loaded.Configuration));
                return;
            }

            if (attempt >= _options.MaximumRetries)
            {
                // Whatever is on disk cannot be trusted right now. The last good
                // snapshot stays published rather than being replaced by an
                // empty one the user never configured.
                _log.Write("[StreamDeck] reload failed; keeping previous layout");
                return;
            }

            await _delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_changeSource is not null)
        {
            _changeSource.Changed -= OnChangeSourceChanged;
            _changeSource.Dispose();
        }

        _shutdown.Cancel();
        _signals.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private void OnChangeSourceChanged(object? sender, EventArgs e) =>
        _signals.Writer.TryWrite(0);

    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in _signals.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                _ = signal;
                await _delay(_options.Debounce, cancellationToken)
                    .ConfigureAwait(false);

                // Everything that arrived while waiting belongs to the same
                // save; one read answers all of it.
                while (_signals.Reader.TryRead(out _))
                {
                }

                await ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Write(
                $"[StreamDeck] watcher stopped ({exception.GetType().Name})");
        }
    }

    /// <summary>
    /// One read attempt. Null means "not trustworthy right now", which covers
    /// both a mid-swap file and a document this build refuses.
    /// </summary>
    private async Task<StreamDeckConfigurationLoadResult?> LoadOnceAsync(
        CancellationToken cancellationToken)
    {
        StreamDeckConfigurationLoadResult loaded;
        try
        {
            loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.Write($"[StreamDeck] read failed ({exception.GetType().Name})");
            return null;
        }

        return loaded.Source is
            StreamDeckConfigurationSource.Loaded or
            StreamDeckConfigurationSource.Defaults
            ? loaded
            : null;
    }

    private void Publish(StreamDeckSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            if (string.Equals(
                    _snapshot.Revision,
                    snapshot.Revision,
                    StringComparison.Ordinal))
            {
                return;
            }

            _snapshot = snapshot;
        }

        _log.Write(
            $"[StreamDeck] layout revision={snapshot.Revision} " +
            $"profile={snapshot.ProfileName ?? "none"} " +
            $"pages={snapshot.PageCount}");
        SnapshotChanged?.Invoke(
            this,
            new StreamDeckSnapshotChangedEventArgs(snapshot));
    }
}
