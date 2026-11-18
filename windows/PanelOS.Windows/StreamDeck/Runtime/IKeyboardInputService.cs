namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Windows virtual-key codes for the keys PanelOS can press. The numeric values
/// are the Win32 VK_* constants, so the input service needs no translation
/// table of its own.
/// </summary>
public enum PanelVirtualKey : ushort
{
    None = 0,

    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Escape = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    ArrowLeft = 0x25,
    ArrowUp = 0x26,
    ArrowRight = 0x27,
    ArrowDown = 0x28,
    PrintScreen = 0x2C,
    Delete = 0x2E,

    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    LeftWindows = 0x5B,

    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    VolumeMute = 0xAD,
    VolumeDown = 0xAE,
    VolumeUp = 0xAF,
    MediaNextTrack = 0xB0,
    MediaPreviousTrack = 0xB1,
    MediaStop = 0xB2,
    MediaPlayPause = 0xB3,
}

public enum KeyTransition
{
    Down,
    Up,
}

public readonly record struct KeyboardInputEvent(
    PanelVirtualKey Key,
    KeyTransition Transition)
{
    public override string ToString() =>
        $"{Key} {(Transition == KeyTransition.Down ? "down" : "up")}";
}

/// <summary>
/// Sends synthesized keystrokes. Abstracted so the chord an action produces can
/// be asserted in a test without pressing anything on the machine running it.
/// </summary>
public interface IKeyboardInputService
{
    /// <summary>
    /// Sends the events in order. Implementations must release every key they
    /// pressed, even when the batch fails part way through.
    /// </summary>
    void Send(IReadOnlyList<KeyboardInputEvent> events);
}
