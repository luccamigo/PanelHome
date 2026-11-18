using PanelOS.Windows.Host;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PanelOS.ControlCenter.Tray;

/// <summary>
/// Actions the tray menu offers. The window and the tray drive the same
/// operations, so both reflect the same state.
/// </summary>
public sealed record TrayCommands(
    Action Open,
    Action ConnectDisplay,
    Action DisconnectDisplay,
    Action StartPanelHost,
    Action RestartPanelHost,
    Action StopPanelHost,
    Action Exit);

/// <summary>
/// Tray icon and its menu. WPF has no tray API, so this is the one place that
/// uses Windows Forms; the icon is taken from the running executable so the app
/// ships no binary asset of its own.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon? _ownedIcon;
    private readonly WinForms.ToolStripItem _startHost;
    private readonly WinForms.ToolStripItem _restartHost;
    private readonly WinForms.ToolStripItem _stopHost;
    private readonly WinForms.ToolStripItem _connectDisplay;
    private readonly WinForms.ToolStripItem _disconnectDisplay;

    public TrayIconController(string tooltip, TrayCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        // The wording matches the window: the same operation must not be called
        // one thing on a card and another in the menu.
        _ownedIcon = TryLoadExecutableIcon();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("TrayOpen"), null, (_, _) => commands.Open());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _startHost = menu.Items.Add(
            Strings.Get("TrayStartHost"),
            null,
            (_, _) => commands.StartPanelHost());
        _restartHost = menu.Items.Add(
            Strings.Get("TrayRestartHost"),
            null,
            (_, _) => commands.RestartPanelHost());
        _stopHost = menu.Items.Add(
            Strings.Get("TrayStopHost"),
            null,
            (_, _) => commands.StopPanelHost());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _connectDisplay = menu.Items.Add(
            Strings.Get("TrayConnectDisplay"),
            null,
            (_, _) => commands.ConnectDisplay());
        _disconnectDisplay = menu.Items.Add(
            Strings.Get("TrayDisconnectDisplay"),
            null,
            (_, _) => commands.DisconnectDisplay());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("TrayExit"), null, (_, _) => commands.Exit());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            Text = tooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => commands.Open();
        UpdateHostState(PanelHostStatus.NotRunning);

        // Until the driver has been read once, neither display action can be
        // known to work, so neither is offered.
        UpdateDisplayState(canConnect: false, canDisconnect: false);
    }

    /// <summary>
    /// Enables only what the current state allows: an agent started outside the
    /// Control Center can be seen but never stopped from here.
    /// </summary>
    public void UpdateHostState(PanelHostStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var owned = status is { IsRunning: true, OwnedByControlCenter: true };
        _startHost.Enabled = status.State is PanelHostProcessState.Stopped
            or PanelHostProcessState.Error;
        _restartHost.Enabled = owned;
        _stopHost.Enabled = owned;

        _notifyIcon.Text = status.State switch
        {
            PanelHostProcessState.Running when owned =>
                Strings.Get("TrayHostRunning"),
            PanelHostProcessState.Running => Strings.Get("TrayHostExternal"),
            PanelHostProcessState.Error => Strings.Get("TrayHostError"),
            _ => Strings.Get("AppTitle"),
        };
    }

    /// <summary>
    /// Mirrors the display card's rule: only the direction the driver can
    /// actually go is enabled, so the menu never offers "connect" to a monitor
    /// that is already connected or to a driver that is not installed.
    /// </summary>
    public void UpdateDisplayState(bool canConnect, bool canDisconnect)
    {
        _connectDisplay.Enabled = canConnect;
        _disconnectDisplay.Enabled = canDisconnect;
    }

    public void ShowMessage(string title, string message) =>
        _notifyIcon.ShowBalloonTip(3000, title, message, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
    }

    private static Drawing.Icon? TryLoadExecutableIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            return executablePath is null
                ? null
                : Drawing.Icon.ExtractAssociatedIcon(executablePath);
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or
                UnauthorizedAccessException)
        {
            return null;
        }
    }
}
