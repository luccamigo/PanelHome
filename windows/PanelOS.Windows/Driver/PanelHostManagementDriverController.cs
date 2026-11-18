using PanelOS.DriverInterop;
using PanelOS.Windows.Management;

namespace PanelOS.Windows.Driver;

/// <summary>
/// Control Center view of the display through medium-IL management v1. It never
/// opens the driver interface; PanelHost owns Desktop and delegates only IOCTLs
/// to the elevated broker.
/// </summary>
public sealed class PanelHostManagementDriverController : IPanelDriverController
{
    private readonly IPanelHostManagementClient _management;
    private readonly IPanelHostManagementDesktopCommands _commands;

    public PanelHostManagementDriverController(
        IPanelHostManagementClient management,
        IPanelHostManagementDesktopCommands commands)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public Task<PanelDriverSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var display = _management.Snapshot?.Display;
        if (display is null)
        {
            return Task.FromResult(new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Unknown,
                Detail = "PanelHost management is disconnected.",
            });
        }

        var mode = display.Width is { } width && display.Height is { } height
            ? new PanelDisplayModeInfo(
                checked((uint)width),
                checked((uint)height),
                checked((uint)(display.RefreshRate ?? 0)))
            : default;
        return Task.FromResult(new PanelDriverSnapshot
        {
            Availability = display.State == "Error"
                ? PanelDriverAvailability.Error
                : PanelDriverAvailability.Available,
            Lifecycle = display.State == "Error"
                ? PanelDriverLifecycle.Unknown
                : PanelDriverLifecycle.Ready,
            Monitor = display.Connected
                ? PanelMonitorConnection.Connected
                : PanelMonitorConnection.Disconnected,
            ProtocolVersion = PanelDisplayDriverContract.ProtocolVersion,
            ActiveMode = mode,
            InterfacePath = "management://PanelHost/display",
            Detail = display.State == "Error"
                ? "PanelHost reported a display lifecycle error."
                : null,
        });
    }

    public async Task<PanelDriverCommandResult> ConnectAsync(
        CancellationToken cancellationToken) =>
        Map(await _commands.StartDesktopAsync(cancellationToken).ConfigureAwait(false));

    public async Task<PanelDriverCommandResult> DisconnectAsync(
        CancellationToken cancellationToken) =>
        Map(await _commands.StopDesktopAsync(cancellationToken).ConfigureAwait(false));

    private static PanelDriverCommandResult Map(
        PanelHostManagementDesktopResult result) =>
        new(
            result.Succeeded
                ? result.AlreadyInDesiredState
                    ? PanelDriverCommandStatus.AlreadyInDesiredState
                    : PanelDriverCommandStatus.Succeeded
                : result.ErrorCode switch
                {
                    "driver_not_found" => PanelDriverCommandStatus.DriverNotInstalled,
                    "driver_access_denied" => PanelDriverCommandStatus.AccessDenied,
                    "driver_not_ready" => PanelDriverCommandStatus.DriverNotReady,
                    "elevation_declined" => PanelDriverCommandStatus.ElevationDeclined,
                    _ => PanelDriverCommandStatus.Failed,
                },
            result.State == "Running"
                ? PanelMonitorConnection.Connected
                : result.State == "Stopped"
                    ? PanelMonitorConnection.Disconnected
                    : PanelMonitorConnection.Unknown,
            result.ErrorCode);
}
