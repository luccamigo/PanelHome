namespace PanelOS.Windows;

/// <summary>
/// Per-user locations shared by every PanelOS Windows component. Nothing here
/// is written inside the repository or under a machine-wide root.
/// </summary>
public static class PanelOSPaths
{
    /// <summary>%LOCALAPPDATA%\PanelOS.</summary>
    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        "PanelOS");

    public static string StreamDeckConfigurationFile { get; } =
        Path.Combine(LocalDataDirectory, "streamdeck.json");

    public static string LoopMediaDirectory { get; } =
        Path.Combine(LocalDataDirectory, "media");

    public static string LoopMediaConfigurationFile { get; } =
        Path.Combine(LocalDataDirectory, "media.json");

    public static string ControlCenterSettingsFile { get; } =
        Path.Combine(LocalDataDirectory, "control-center.json");

    /// <summary>Button images, addressed by icon id.</summary>
    public static string IconCacheDirectory { get; } =
        Path.Combine(LocalDataDirectory, "icons");

    /// <summary>Rotating PanelHost output, bounded in size and count.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(LocalDataDirectory, "logs");

    public static string RedactForLog(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = text;
        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            result = result.Replace(profile, "<user-profile>", StringComparison.OrdinalIgnoreCase);
        }

        var machine = Environment.MachineName;
        if (machine.Length >= 3)
        {
            result = result.Replace(machine, "<machine>", StringComparison.OrdinalIgnoreCase);
        }

        var usbPath = result.IndexOf(@"\\?\usb#", StringComparison.OrdinalIgnoreCase);
        if (usbPath >= 0)
        {
            var end = result.IndexOfAny([' ', '\t', '\r', '\n'], usbPath);
            result = result.Remove(
                    usbPath,
                    (end < 0 ? result.Length : end) - usbPath)
                .Insert(usbPath, "<usb-device>");
        }
        return result;
    }
}
