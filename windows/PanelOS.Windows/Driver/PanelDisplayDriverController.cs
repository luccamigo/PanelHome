using PanelOS.DriverInterop;

namespace PanelOS.Windows.Driver;

/// <summary>
/// Adapts the PanelHost driver client to <see cref="IPanelDriverController"/>.
/// </summary>
/// <remarks>
/// This is a thin control plane: it issues GET_STATUS / CONNECT_DISPLAY /
/// DISCONNECT_DISPLAY and translates the result. It deliberately does not
/// reimplement the PanelHost desktop lifecycle (compensating disconnects,
/// session state). Both commands are idempotent in the driver contract and the
/// driver stays the single source of truth about the virtual monitor, so
/// PanelHost observes whatever the Control Center did on its next GET_STATUS.
/// </remarks>
public sealed class PanelDisplayDriverController : IPanelDriverController
{
    private readonly IPanelDisplayDriverClient _client;

    public PanelDisplayDriverController()
        : this(new PanelDisplayDriverClient())
    {
    }

    internal PanelDisplayDriverController(IPanelDisplayDriverClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Contract version this build was compiled against
    /// (PANEL_DISPLAY_PROTOCOL_VERSION in Public.h).
    /// </summary>
    public static uint ExpectedProtocolVersion =>
        PanelDisplayDriverContract.ProtocolVersion;

    public static Guid DeviceInterfaceGuid =>
        PanelDisplayDriverContract.DeviceInterfaceGuid;

    public async Task<PanelDriverSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await _client.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            var modes = new PanelDisplayModeInfo[status.Modes.Count];
            for (var index = 0; index < modes.Length; index++)
            {
                modes[index] = ToModeInfo(status.Modes[index]);
            }

            return new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Available,
                Lifecycle = ToLifecycle(status.DriverState),
                Monitor = ToMonitorConnection(status.MonitorState),
                ProtocolVersion = status.ProtocolVersion,
                ActiveMode = ToModeInfo(status.ActiveMode),
                SupportedModes = modes,
                ConnectCount = status.ConnectCount,
                InterfacePath = _client.InterfacePath,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PanelDisplayDriverException exception)
        {
            return new PanelDriverSnapshot
            {
                Availability = ToAvailability(exception.Kind),
                InterfacePath = exception.InterfacePath,
                Detail = exception.Message,
            };
        }
        catch (Exception exception)
        {
            return new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Error,
                InterfacePath = _client.InterfacePath,
                Detail = $"{exception.GetType().Name}: {exception.Message}",
            };
        }
    }

    public Task<PanelDriverCommandResult> ConnectAsync(
        CancellationToken cancellationToken) =>
        RunCommandAsync(
            _client.ConnectAsync,
            PanelDisplayCommandResult.AlreadyConnected,
            cancellationToken);

    public Task<PanelDriverCommandResult> DisconnectAsync(
        CancellationToken cancellationToken) =>
        RunCommandAsync(
            _client.DisconnectAsync,
            PanelDisplayCommandResult.AlreadyDisconnected,
            cancellationToken);

    private static async Task<PanelDriverCommandResult> RunCommandAsync(
        Func<CancellationToken, Task<PanelDisplayCommandResponse>> command,
        PanelDisplayCommandResult idempotentResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await command(cancellationToken).ConfigureAwait(false);
            var monitor = ToMonitorConnection(response.MonitorState);
            if (response.Result == idempotentResult)
            {
                return new PanelDriverCommandResult(
                    PanelDriverCommandStatus.AlreadyInDesiredState,
                    monitor);
            }

            return response.Result switch
            {
                PanelDisplayCommandResult.Ok => new PanelDriverCommandResult(
                    PanelDriverCommandStatus.Succeeded,
                    monitor),
                PanelDisplayCommandResult.AdapterNotReady =>
                    new PanelDriverCommandResult(
                        PanelDriverCommandStatus.DriverNotReady,
                        monitor,
                        "O driver ainda está inicializando."),
                _ => new PanelDriverCommandResult(
                    PanelDriverCommandStatus.Failed,
                    monitor,
                    $"O driver respondeu {response.Result}."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PanelDisplayDriverException exception)
        {
            return new PanelDriverCommandResult(
                exception.Kind switch
                {
                    PanelDisplayDriverFailureKind.NotFound =>
                        PanelDriverCommandStatus.DriverNotInstalled,
                    PanelDisplayDriverFailureKind.AccessDenied =>
                        PanelDriverCommandStatus.AccessDenied,
                    _ => PanelDriverCommandStatus.Failed,
                },
                PanelMonitorConnection.Unknown,
                exception.Message);
        }
        catch (Exception exception)
        {
            return new PanelDriverCommandResult(
                PanelDriverCommandStatus.Failed,
                PanelMonitorConnection.Unknown,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static PanelDriverAvailability ToAvailability(
        PanelDisplayDriverFailureKind kind) => kind switch
        {
            PanelDisplayDriverFailureKind.NotFound =>
                PanelDriverAvailability.NotInstalled,
            PanelDisplayDriverFailureKind.AccessDenied =>
                PanelDriverAvailability.AccessDenied,
            _ => PanelDriverAvailability.Error,
        };

    private static PanelDriverLifecycle ToLifecycle(
        PanelDisplayDriverState state) => state switch
        {
            PanelDisplayDriverState.Initializing =>
                PanelDriverLifecycle.Initializing,
            PanelDisplayDriverState.Ready => PanelDriverLifecycle.Ready,
            PanelDisplayDriverState.Stopped => PanelDriverLifecycle.Stopped,
            _ => PanelDriverLifecycle.Unknown,
        };

    private static PanelMonitorConnection ToMonitorConnection(
        PanelDisplayMonitorState state) => state switch
        {
            PanelDisplayMonitorState.Connected => PanelMonitorConnection.Connected,
            PanelDisplayMonitorState.Disconnected =>
                PanelMonitorConnection.Disconnected,
            _ => PanelMonitorConnection.Unknown,
        };

    private static PanelDisplayModeInfo ToModeInfo(PanelDisplayMode mode) =>
        new(mode.Width, mode.Height, mode.RefreshHz);
}
