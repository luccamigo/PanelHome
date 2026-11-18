using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PanelOS.ControlCenter.Services;

/// <summary>Flashes the taskbar button until the window is brought to the front (no sound, no toast). Best effort: a failure is ignored.</summary>
public static class WindowFlash
{
    private const uint FlashStop = 0;
    private const uint FlashTray = 2;
    private const uint FlashUntilForeground = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Only when the window is not already the one in front.</summary>
    public static void Start(Window window)
    {
        if (window.IsActive) return;
        Send(window, FlashTray | FlashUntilForeground);
    }

    public static void Stop(Window window) => Send(window, FlashStop);

    private static void Send(Window window, uint flags)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = flags, Count = uint.MaxValue };
            FlashWindowEx(ref info);
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { }
    }
}