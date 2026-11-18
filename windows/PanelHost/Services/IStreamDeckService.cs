using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelHost.Services;

/// <summary>
/// What a client session needs from the Stream Deck: the current layout, a way
/// to hear about changes, a way to run a button and a way to fetch an icon.
/// </summary>
/// <remarks>
/// The session never sees the configuration store, the executor or the icon
/// cache directly, which keeps the "an id in, an outcome out" shape of the
/// remote surface visible in one place.
/// </remarks>
internal interface IStreamDeckService
{
    /// <summary>Whether the feature may be announced in the handshake.</summary>
    bool IsAvailable { get; }

    StreamDeckSnapshot Snapshot { get; }

    event EventHandler<StreamDeckSnapshotChangedEventArgs>? SnapshotChanged;

    /// <summary>
    /// Runs the button named by <paramref name="buttonId"/> as it exists in
    /// <paramref name="snapshot"/>.
    /// </summary>
    Task<StreamDeckActionResult> InvokeAsync(
        StreamDeckSnapshot snapshot,
        string buttonId,
        CancellationToken cancellationToken);

    /// <summary>PNG bytes for a cached icon, or null when there are none.</summary>
    Task<byte[]?> TryReadIconAsync(string iconId, CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in used when the host runs without Stream Deck support. It announces
/// nothing, so no session ever negotiates the feature.
/// </summary>
internal sealed class UnavailableStreamDeckService : IStreamDeckService
{
    public static UnavailableStreamDeckService Instance { get; } = new();

    public bool IsAvailable => false;

    public StreamDeckSnapshot Snapshot => StreamDeckSnapshot.Empty;

    public event EventHandler<StreamDeckSnapshotChangedEventArgs>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public Task<StreamDeckActionResult> InvokeAsync(
        StreamDeckSnapshot snapshot,
        string buttonId,
        CancellationToken cancellationToken) =>
        Task.FromResult(StreamDeckActionResult.Error(
            StreamDeckErrorCodes.ConfigurationUnavailable,
            "O Stream Deck não está disponível neste host."));

    public Task<byte[]?> TryReadIconAsync(
        string iconId,
        CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);
}
