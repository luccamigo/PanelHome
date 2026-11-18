using PanelOS.Windows.StreamDeck;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// Short, human readable summary of an action for the grid and the editor.
/// </summary>
public static class PanelActionDescriber
{
    public static string NoAction => Strings.Get("ActionKindNone");

    public static string Describe(PanelAction? action) => action switch
    {
        null => NoAction,
        LaunchApplicationAction launch => launch.IsConfigured
            ? Path.GetFileName(launch.ExecutablePath!.AsSpan()).ToString()
            : Strings.Get("ActionApplicationNotConfigured"),
        OpenUrlAction openUrl => openUrl.IsConfigured
            ? openUrl.Url!
            : Strings.Get("ActionUrlNotConfigured"),
        KeyboardShortcutAction shortcut => shortcut.IsConfigured
            ? shortcut.Describe()
            : Strings.Get("ActionShortcutNotConfigured"),
        MediaCommandAction media => Strings.Format("ActionMediaFormat", media.Command),
        VolumeCommandAction volume => Strings.Format("ActionVolumeFormat", volume.Command),
        PanelModeAction panelMode =>
            Strings.Format("ActionPanelModeFormat", panelMode.Mode),
        UnknownPanelAction unknown =>
            Strings.Format("ActionUnknownFormat", unknown.RawKind),
        _ => NoAction,
    };

    public static string DescribeKind(PanelActionKind? kind) => kind switch
    {
        null => NoAction,
        PanelActionKind.LaunchApplication => Strings.Get("ActionKindLaunchApplication"),
        PanelActionKind.OpenUrl => Strings.Get("ActionKindOpenUrl"),
        PanelActionKind.KeyboardShortcut => Strings.Get("ActionKindKeyboardShortcut"),
        PanelActionKind.MediaCommand => Strings.Get("ActionKindMediaCommand"),
        PanelActionKind.VolumeCommand => Strings.Get("ActionKindVolumeCommand"),
        PanelActionKind.PanelModeCommand => Strings.Get("ActionKindPanelMode"),
        _ => Strings.Get("ActionKindUnknown"),
    };
}
