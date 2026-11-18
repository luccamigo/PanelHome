namespace PanelOS.Windows.Development;

public enum AdbDeviceState
{
    Unknown,
    NoDevice,
    Unauthorized,
    Offline,

    /// <summary>More than one target; the user has to disconnect one.</summary>
    MultipleDevices,

    /// <summary>A single authorised USB device.</summary>
    Ready,
}

public sealed record AdbStatus
{
    public required bool AdbFound { get; init; }

    public string? AdbPath { get; init; }

    public AdbDeviceState Device { get; init; } = AdbDeviceState.Unknown;

    public string? DeviceSerial { get; init; }

    public string? Detail { get; init; }

    public bool IsReady => AdbFound && Device == AdbDeviceState.Ready;

    public static AdbStatus NotFound(string detail) =>
        new() { AdbFound = false, Detail = detail };
}

public sealed record AdbReverseResult(
    bool Success,
    string Message,
    IReadOnlyList<int> Ports);

/// <summary>
/// USB transport plumbing for development only.
/// </summary>
/// <remarks>
/// <para>
/// ADB is how a tablet reaches PanelHost <em>today</em>: the app connects to
/// 127.0.0.1 and <c>adb reverse</c> carries it over the cable. That is a
/// development arrangement, not the product's transport, so it lives in its own
/// namespace, behind its own interface, and nothing in the Control Center's
/// normal operation depends on it.
/// </para>
/// <para>
/// The UI only shows any of this while Development Mode is on. When the final
/// USB transport replaces ADB, this interface is deleted and no other component
/// has to change.
/// </para>
/// </remarks>
public interface IAdbDevelopmentTransport
{
    /// <summary>Ports PanelHost listens on: control and video.</summary>
    IReadOnlyList<int> Ports { get; }

    Task<AdbStatus> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>adb reverse</c> for every port and confirms the result.
    /// </summary>
    Task<AdbReverseResult> ConfigureReverseAsync(CancellationToken cancellationToken);
}
