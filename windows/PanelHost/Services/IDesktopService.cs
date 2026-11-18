namespace PanelHost.Services;

public enum DesktopServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

public sealed record DesktopRequest(
    int? Width = null,
    int? Height = null,
    int? RefreshRate = null);

public sealed record DesktopServiceSnapshot(
    DesktopServiceState State,
    string? ErrorCode = null,
    int? Width = null,
    int? Height = null,
    int? RefreshRate = null);

public sealed record DesktopStartResult(
    bool Succeeded,
    bool AlreadyRunning,
    DesktopServiceState State,
    string? ErrorCode = null);

public sealed record DesktopStopResult(
    bool Succeeded,
    bool AlreadyStopped,
    DesktopServiceState State,
    string? ErrorCode = null);

public sealed class DesktopStateChangedEventArgs : EventArgs
{
    public DesktopStateChangedEventArgs(DesktopServiceSnapshot state)
    {
        State = state;
    }

    public DesktopServiceSnapshot State { get; }
}

/// <summary>
/// Owns the single Desktop lifecycle for PanelHost. Both the production
/// PanelDisplayDriverDesktopService and the development FakeDesktopService
/// implement this contract, so the protocol/session layer is backend-agnostic.
/// </summary>
/// <remarks>
/// Implementations must observe the supplied cancellation tokens and publish
/// their terminal state before an operation task completes. Session teardown
/// waits for in-flight operations before cleanup so an obsolete start cannot
/// restore a Desktop after it has been stopped.
/// </remarks>
public interface IDesktopService : IAsyncDisposable
{
    DesktopServiceState State { get; }

    DesktopServiceSnapshot Snapshot { get; }

    string? ErrorCode { get; }

    event EventHandler<DesktopStateChangedEventArgs>? StateChanged;

    Task<DesktopStartResult> StartAsync(
        DesktopRequest request,
        CancellationToken cancellationToken);

    Task<DesktopStopResult> StopAsync(CancellationToken cancellationToken);
}
