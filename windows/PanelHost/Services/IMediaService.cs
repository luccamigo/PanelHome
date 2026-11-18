using PanelHost.Protocol;

namespace PanelHost.Services;

public sealed class MediaStateChangedEventArgs : EventArgs
{
    public MediaStateChangedEventArgs(
        MediaStateMessage state,
        MediaArtworkMessage artwork,
        bool artworkChanged)
    {
        State = state;
        Artwork = artwork;
        ArtworkChanged = artworkChanged;
    }

    public MediaStateMessage State { get; }

    public MediaArtworkMessage Artwork { get; }

    public bool ArtworkChanged { get; }
}

public interface IMediaService : IAsyncDisposable
{
    MediaStateMessage CurrentState { get; }

    MediaArtworkMessage CurrentArtwork { get; }

    event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> ExecuteCommandAsync(MediaCommand command, CancellationToken cancellationToken);
}
