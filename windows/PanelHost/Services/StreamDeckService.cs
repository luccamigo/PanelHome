using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Icons;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelHost.Services;

/// <summary>
/// The Stream Deck as PanelHost uses it: a provider that keeps the
/// configuration current, the shared executor that runs a button, and the
/// shared icon cache.
/// </summary>
/// <remarks>
/// <para>
/// Invocation resolves the id inside the snapshot the tablet echoed a revision
/// for, then hands the resolved button to
/// <see cref="IStreamDeckActionExecutor.ExecuteButtonAsync"/>. Resolving
/// against the snapshot rather than re-reading the file closes the window the
/// revision check exists to close: between "the revision still matches" and
/// "run it", a save could otherwise repoint the same id at a different action.
/// </para>
/// <para>
/// No action logic lives here. What the executor does with a button, and which
/// targets it refuses, stays in PanelOS.Windows and is the same code the
/// Control Center's "test action" runs.
/// </para>
/// </remarks>
internal sealed class StreamDeckService : IStreamDeckService, IAsyncDisposable
{
    private readonly StreamDeckConfigurationProvider _provider;
    private readonly IStreamDeckActionExecutor _executor;
    private readonly IStreamDeckIconCache _icons;
    private readonly IStreamDeckLog _log;
    private readonly int _maximumIconBytes;

    public StreamDeckService(
        StreamDeckConfigurationProvider provider,
        IStreamDeckActionExecutor executor,
        IStreamDeckIconCache icons,
        IStreamDeckLog? log = null,
        int maximumIconBytes = StreamDeckIconCache.MaximumIconBytes)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumIconBytes);
        _provider = provider;
        _executor = executor;
        _icons = icons;
        _log = log ?? NullStreamDeckLog.Instance;
        _maximumIconBytes = maximumIconBytes;
    }

    public bool IsAvailable => true;

    public StreamDeckSnapshot Snapshot => _provider.Snapshot;

    public event EventHandler<StreamDeckSnapshotChangedEventArgs>? SnapshotChanged
    {
        add => _provider.SnapshotChanged += value;
        remove => _provider.SnapshotChanged -= value;
    }

    public async Task<StreamDeckActionResult> InvokeAsync(
        StreamDeckSnapshot snapshot,
        string buttonId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);

        _log.Write($"[StreamDeck] invoke button={buttonId}");
        if (snapshot.FindPublishedButton(buttonId) is not { } button)
        {
            _log.Write(
                $"[StreamDeck] failed code={StreamDeckErrorCodes.ButtonNotFound}");
            return StreamDeckActionResult.Error(
                StreamDeckErrorCodes.ButtonNotFound,
                "O botão não existe na configuração publicada deste PC.");
        }

        // The executor logs the outcome itself, in the same narrow form it uses
        // for the Control Center; repeating it here would only duplicate lines.
        return await _executor
            .ExecuteButtonAsync(button, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<byte[]?> TryReadIconAsync(
        string iconId,
        CancellationToken cancellationToken) =>
        _icons.TryReadAsync(iconId, _maximumIconBytes, cancellationToken);

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
