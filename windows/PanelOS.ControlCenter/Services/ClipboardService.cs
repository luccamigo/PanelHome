using System.Windows;

namespace PanelOS.ControlCenter.Services;

public interface IClipboardService
{
    void SetText(string text);
}

public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another application can hold the clipboard open; losing a copy is
            // not worth an error dialog.
        }
    }
}
