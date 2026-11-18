using Microsoft.Win32;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.Services;

public interface ILoopMediaFilePicker
{
    string? PickFile();
}

public sealed class LoopMediaFilePicker : ILoopMediaFilePicker
{
    public string? PickFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("NavLoopMedia"),
            Filter = "GIF ou MP4|*.gif;*.mp4",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
