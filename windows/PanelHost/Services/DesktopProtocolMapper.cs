using PanelHost.Protocol;

namespace PanelHost.Services;

internal static class DesktopProtocolMapper
{
    public static DesktopStateMessage CreateStateMessage(
        DesktopServiceSnapshot snapshot,
        long? touchGeneration = null) =>
        new()
        {
            State = snapshot.State switch
            {
                DesktopServiceState.Stopped => DesktopStates.Stopped,
                DesktopServiceState.Starting => DesktopStates.Starting,
                DesktopServiceState.Running => DesktopStates.Running,
                DesktopServiceState.Stopping => DesktopStates.Stopping,
                DesktopServiceState.Error => DesktopStates.Error,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(snapshot),
                    snapshot.State,
                    null),
            },
            ErrorCode = snapshot.State == DesktopServiceState.Error
                ? snapshot.ErrorCode
                : null,
            Generation = snapshot.State == DesktopServiceState.Running
                ? touchGeneration
                : null,
        };

    public static DesktopStateMessage CreateErrorMessage(string errorCode) =>
        new()
        {
            State = DesktopStates.Error,
            ErrorCode = errorCode,
        };
}
