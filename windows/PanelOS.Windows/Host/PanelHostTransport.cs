namespace PanelOS.Windows.Host;

/// <summary>
/// Carrier the agent opens for the tablet, passed to PanelHost as
/// <c>--transport</c>.
/// </summary>
/// <remarks>
/// <para>
/// The agent defaults to ADB/TCP when it is launched with no arguments, which
/// is the development carrier: it needs a session credential and an
/// <c>adb reverse</c> pair before a tablet can reach it. Launching the product
/// path therefore has to be explicit rather than implied by silence.
/// </para>
/// <para>
/// The two are not interchangeable at runtime: switching carrier means
/// restarting the agent, which is why this is a launch option and not a
/// command.
/// </para>
/// </remarks>
public enum PanelHostTransport
{
    /// <summary>
    /// The product carrier: the tablet's own USB accessory link. It needs no
    /// credential and no host-side setup, so it is what a plain start uses.
    /// </summary>
    NativeUsb,

    /// <summary>
    /// The development carrier over ADB port forwarding. It requires a
    /// development-session token in the agent's environment.
    /// </summary>
    AdbTcp,
}

public static class PanelHostTransportExtensions
{
    /// <summary>The value PanelHost's <c>--transport</c> switch expects.</summary>
    public static string ToCommandLineValue(this PanelHostTransport transport) =>
        transport switch
        {
            PanelHostTransport.AdbTcp => "adb",
            _ => "usb",
        };

    /// <summary>
    /// Reads a persisted or configured value, falling back to the product
    /// carrier for anything unrecognised rather than refusing to start.
    /// </summary>
    public static PanelHostTransport ParseTransport(string? value) =>
        string.Equals(value, "adb", StringComparison.OrdinalIgnoreCase)
            ? PanelHostTransport.AdbTcp
            : PanelHostTransport.NativeUsb;
}
