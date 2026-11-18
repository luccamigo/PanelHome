using PanelHost.Protocol;

namespace PanelHost.Services;

public sealed class VolumeStateChangedEventArgs : EventArgs
{
    public VolumeStateChangedEventArgs(VolumeStateMessage state)
    {
        State = state;
    }

    public VolumeStateMessage State { get; }
}

public interface IVolumeService : IAsyncDisposable
{
    VolumeStateMessage CurrentState { get; }

    event EventHandler<VolumeStateChangedEventArgs>? StateChanged;

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> SetVolumeAsync(
        int volumePercent,
        CancellationToken cancellationToken);

    Task<bool> ToggleMuteAsync(CancellationToken cancellationToken);
}
