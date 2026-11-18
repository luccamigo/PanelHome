namespace PanelOS.Windows.Driver;

/// <summary>
/// Control-plane view of PanelDisplayDriver for user interfaces. Production
/// Control Center composition uses PanelHost management; diagnostic adapters
/// may talk directly to the device. PanelDisplayCtl.exe is never spawned.
/// </summary>
public interface IPanelDriverController
{
    Task<PanelDriverSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<PanelDriverCommandResult> ConnectAsync(CancellationToken cancellationToken);

    Task<PanelDriverCommandResult> DisconnectAsync(CancellationToken cancellationToken);
}
