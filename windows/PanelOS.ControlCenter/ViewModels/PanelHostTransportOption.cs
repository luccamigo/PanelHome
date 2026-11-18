using PanelOS.Windows.Host;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// One carrier the agent can be launched with, named for what it is rather
/// than for the switch it becomes.
/// </summary>
/// <remarks>
/// The value the agent's command line expects - "usb" or "adb" - stays inside
/// <see cref="PanelHostTransport"/>; the page shows a sentence, because the
/// choice has consequences ("needs adb reverse") that a three-letter switch
/// does not communicate.
/// </remarks>
public sealed class PanelHostTransportOption
{
    private PanelHostTransportOption(
        PanelHostTransport value,
        string label,
        string description)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public static IReadOnlyList<PanelHostTransportOption> Options { get; } =
    [
        new(
            PanelHostTransport.NativeUsb,
            Strings.Get("SettingsTransportUsb"),
            Strings.Get("SettingsTransportUsbHint")),
        new(
            PanelHostTransport.AdbTcp,
            Strings.Get("SettingsTransportAdb"),
            Strings.Get("SettingsTransportAdbHint")),
    ];

    public PanelHostTransport Value { get; }

    public string Label { get; }

    /// <summary>What the choice costs and what it needs, in one line.</summary>
    public string Description { get; }

    public static PanelHostTransportOption For(PanelHostTransport value) =>
        Options.First(option => option.Value == value);
}
