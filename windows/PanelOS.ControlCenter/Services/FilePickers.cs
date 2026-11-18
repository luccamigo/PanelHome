using Microsoft.Win32;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.Services;

/// <summary>
/// Chooses an image file for a button icon. Abstracted so the Stream Deck
/// editor stays testable without a dialog.
/// </summary>
public interface IIconFilePicker
{
    string? PickImageFile();
}

public sealed record ApplicationPick(string ExecutablePath, string DisplayName);

/// <summary>
/// Chooses an application to launch.
/// </summary>
/// <remarks>
/// A file dialog is the whole feature on purpose. Scanning Program Files, the
/// Start Menu and the registry to build an app catalog is a different project,
/// and the picker already answers the only question the editor has: which
/// executable did the user mean?
/// </remarks>
public interface IApplicationPicker
{
    ApplicationPick? PickApplication();
}

public sealed class IconFilePicker : IIconFilePicker
{
    public string? PickImageFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("PickerIconTitle"),
            Filter = Strings.Get("PickerIconFilter"),
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public sealed class ApplicationPickerService : IApplicationPicker
{
    public ApplicationPick? PickApplication()
    {
        // Only .exe: the runtime refuses scripts and shortcuts, so offering
        // them here would just produce a button that fails later.
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("PickerApplicationTitle"),
            Filter = Strings.Get("PickerApplicationFilter"),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var path = dialog.FileName;
        return new ApplicationPick(path, CreateDisplayName(path));
    }

    /// <summary>
    /// A readable name for the label: the file description when the executable
    /// has one, otherwise its file name without the extension.
    /// </summary>
    internal static string CreateDisplayName(string executablePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(
                executablePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription.Trim();
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }

        return Path.GetFileNameWithoutExtension(executablePath);
    }
}
