namespace PanelOS.Windows.Driver;

/// <summary>
/// Whether the PanelDisplayDriver device interface could be reached at all.
/// This is deliberately separate from the driver's own lifecycle: an installed
/// driver that is still initialising is Available/Initializing, while a missing
/// driver has no lifecycle to report.
/// </summary>
public enum PanelDriverAvailability
{
    /// <summary>No query has completed yet.</summary>
    Unknown,
    Available,
    NotInstalled,
    AccessDenied,
    Error,
}

public enum PanelDriverLifecycle
{
    Unknown,
    Initializing,
    Ready,
    Stopped,
}

public enum PanelMonitorConnection
{
    Unknown,
    Disconnected,
    Connected,
}

public readonly record struct PanelDisplayModeInfo(
    uint Width,
    uint Height,
    uint RefreshHz)
{
    /// <summary>
    /// GET_STATUS reports an all-zero mode while the monitor is disconnected or
    /// connected but not yet activated by Windows.
    /// </summary>
    public bool IsSpecified => Width != 0 && Height != 0;

    public override string ToString() =>
        IsSpecified ? $"{Width} × {Height} @ {RefreshHz} Hz" : "—";
}

/// <summary>
/// Read-only view of IOCTL_PANEL_GET_STATUS plus the outcome of reaching the
/// interface. Failures are reported here instead of thrown so a Control Center
/// window can open and stay usable without an installed driver.
/// </summary>
public sealed record PanelDriverSnapshot
{
    public required PanelDriverAvailability Availability { get; init; }

    public PanelDriverLifecycle Lifecycle { get; init; } = PanelDriverLifecycle.Unknown;

    public PanelMonitorConnection Monitor { get; init; } = PanelMonitorConnection.Unknown;

    public uint ProtocolVersion { get; init; }

    public PanelDisplayModeInfo ActiveMode { get; init; }

    public IReadOnlyList<PanelDisplayModeInfo> SupportedModes { get; init; } = [];

    public uint ConnectCount { get; init; }

    public string? InterfacePath { get; init; }

    /// <summary>Human readable detail for a failure, otherwise null.</summary>
    public string? Detail { get; init; }

    public static PanelDriverSnapshot Unknown { get; } =
        new() { Availability = PanelDriverAvailability.Unknown };

    public bool IsAvailable => Availability == PanelDriverAvailability.Available;

    public bool IsReady =>
        IsAvailable && Lifecycle == PanelDriverLifecycle.Ready;

    public bool IsMonitorConnected =>
        Monitor == PanelMonitorConnection.Connected;
}

public enum PanelDriverCommandStatus
{
    Succeeded,

    /// <summary>
    /// CONNECT/DISCONNECT are idempotent in the driver contract; asking for the
    /// state that already holds is a success, not an error.
    /// </summary>
    AlreadyInDesiredState,
    DriverNotInstalled,
    AccessDenied,
    DriverNotReady,

    /// <summary>The Windows elevation prompt for the display broker was not approved.</summary>
    ElevationDeclined,
    Failed,
}

public sealed record PanelDriverCommandResult(
    PanelDriverCommandStatus Status,
    PanelMonitorConnection Monitor,
    string? Detail = null)
{
    public bool Succeeded => Status is
        PanelDriverCommandStatus.Succeeded or
        PanelDriverCommandStatus.AlreadyInDesiredState;
}
