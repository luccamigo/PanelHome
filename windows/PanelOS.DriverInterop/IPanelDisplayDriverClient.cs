namespace PanelOS.DriverInterop;

public interface IPanelDisplayDriverClient
{
    /// <summary>
    /// Interface path used by the most recent operation. It remains available
    /// after the operation for diagnostics, even though its handle is closed.
    /// </summary>
    string? InterfacePath { get; }

    Task<PanelDisplayDriverStatus> GetStatusAsync(
        CancellationToken cancellationToken);

    Task<PanelDisplayCommandResponse> ConnectAsync(
        CancellationToken cancellationToken);

    Task<PanelDisplayCommandResponse> DisconnectAsync(
        CancellationToken cancellationToken);
}
