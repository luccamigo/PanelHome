namespace PanelHost.Video;

internal sealed class VideoSessionLifetime
{
    internal event EventHandler? ControlDisconnected;

    internal void NotifyControlDisconnected() =>
        ControlDisconnected?.Invoke(this, EventArgs.Empty);
}
