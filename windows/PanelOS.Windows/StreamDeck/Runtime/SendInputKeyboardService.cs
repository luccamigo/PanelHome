using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Synthesizes keystrokes with SendInput. SendKeys is deliberately not used:
/// it drives the focused window through a parsed string, while SendInput posts
/// real virtual-key events to the system input queue, which is what a global
/// shortcut such as Win+D or Win+Shift+S needs.
/// </summary>
public sealed class SendInputKeyboardService : IKeyboardInputService
{
    /// <summary>
    /// Keys that must carry KEYEVENTF_EXTENDEDKEY. Without it the navigation
    /// cluster is reported as its numeric-keypad twin.
    /// </summary>
    private static readonly PanelVirtualKey[] ExtendedKeys =
    [
        PanelVirtualKey.ArrowLeft,
        PanelVirtualKey.ArrowUp,
        PanelVirtualKey.ArrowRight,
        PanelVirtualKey.ArrowDown,
        PanelVirtualKey.Home,
        PanelVirtualKey.End,
        PanelVirtualKey.PageUp,
        PanelVirtualKey.PageDown,
        PanelVirtualKey.Delete,
        PanelVirtualKey.PrintScreen,
    ];

    public void Send(IReadOnlyList<KeyboardInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        var inputs = new NativeMethods.Input[events.Count];
        for (var index = 0; index < events.Count; index++)
        {
            inputs[index] = CreateInput(events[index]);
        }

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (sent == inputs.Length)
        {
            return;
        }

        // A partial batch can leave modifiers latched for every other
        // application, so release whatever this call may have pressed before
        // reporting the failure.
        var error = Marshal.GetLastWin32Error();
        ReleasePressedKeys(events, (int)sent);
        throw new Win32Exception(
            error,
            $"SendInput enviou {sent} de {inputs.Length} eventos de teclado.");
    }

    private static void ReleasePressedKeys(
        IReadOnlyList<KeyboardInputEvent> events,
        int delivered)
    {
        var stillDown = new List<PanelVirtualKey>();
        for (var index = 0; index < delivered && index < events.Count; index++)
        {
            var current = events[index];
            if (current.Transition == KeyTransition.Down)
            {
                stillDown.Add(current.Key);
            }
            else
            {
                stillDown.Remove(current.Key);
            }
        }

        if (stillDown.Count == 0)
        {
            return;
        }

        stillDown.Reverse();
        var releases = new NativeMethods.Input[stillDown.Count];
        for (var index = 0; index < stillDown.Count; index++)
        {
            releases[index] = CreateInput(
                new KeyboardInputEvent(stillDown[index], KeyTransition.Up));
        }

        _ = NativeMethods.SendInput(
            (uint)releases.Length,
            releases,
            Marshal.SizeOf<NativeMethods.Input>());
    }

    private static NativeMethods.Input CreateInput(KeyboardInputEvent value)
    {
        var flags = NativeMethods.KeyEventFlags.None;
        if (value.Transition == KeyTransition.Up)
        {
            flags |= NativeMethods.KeyEventFlags.KeyUp;
        }

        if (Array.IndexOf(ExtendedKeys, value.Key) >= 0)
        {
            flags |= NativeMethods.KeyEventFlags.ExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = (ushort)value.Key,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    private static class NativeMethods
    {
        internal const uint InputKeyboard = 1;

        [Flags]
        internal enum KeyEventFlags : uint
        {
            None = 0,
            ExtendedKey = 0x0001,
            KeyUp = 0x0002,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal KeyEventFlags Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HardwareInput
        {
            internal uint Message;
            internal ushort ParamL;
            internal ushort ParamH;
        }

        // The union is as wide as its largest member; MOUSEINPUT is what sets
        // the size of INPUT, so it has to be part of the layout.
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal MouseInput Mouse;

            [FieldOffset(0)]
            internal KeyboardInput Keyboard;

            [FieldOffset(0)]
            internal HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint count,
            [MarshalAs(UnmanagedType.LPArray)] Input[] inputs,
            int size);
    }
}
